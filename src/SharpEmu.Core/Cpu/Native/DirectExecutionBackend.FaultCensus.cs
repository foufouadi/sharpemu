// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Globalization;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Counts the recoverable host faults taken on guest threads, by guest RIP and
/// by the handler that resolved them.
/// </summary>
/// <remarks>
/// Windows has no alternate exception-dispatch stack, so every fault dispatched
/// while guest code runs writes an EXCEPTION_RECORD and a CONTEXT below RSP and
/// destroys the 128 bytes of System V red zone the guest may be using. The
/// red-zone patcher shifts RSP around instructions it can relocate; the ones it
/// cannot are listed by <c>SHARPEMU_REDZONE_TRACE</c>. This census answers the
/// other half of the question: which guest instructions actually fault.
///
/// <c>SHARPEMU_FAULT_CENSUS=1</c> enables counting and prints a summary on
/// shutdown and on a fatal access violation. <c>SHARPEMU_FAULT_WATCH</c> takes
/// addresses and <c>start-end</c> ranges and logs every single fault whose RIP
/// falls inside one, with the faulting address and RSP.
/// </remarks>
public sealed partial class DirectExecutionBackend
{
	internal enum GuestFaultHandler
	{
		GuestInt41,
		ImageWriteTracker,
		AuxiliaryThreadExecute,
		LazyCommit,
		AllocatorHole,
		GpuFault,
		IllegalInstruction,
		AmdCompat,
		BenignHostDebug,
	}

	private const int GuestFaultHandlerCount = 9;

	private static readonly bool _faultCensusEnabled =
		string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_FAULT_CENSUS"), "1", StringComparison.Ordinal);

	private static readonly (ulong Start, ulong End)[] _faultWatchRanges = ParseFaultWatchRanges();

	// Values are allocated once per distinct RIP, so a fault on a RIP already
	// seen only does an interlocked increment inside the vectored handler.
	private static readonly ConcurrentDictionary<ulong, long[]> _faultCensus = new();

	private static long _faultCensusTotal;

	private static (ulong Start, ulong End)[] ParseFaultWatchRanges()
	{
		var raw = Environment.GetEnvironmentVariable("SHARPEMU_FAULT_WATCH");
		if (string.IsNullOrWhiteSpace(raw))
		{
			return [];
		}

		var ranges = new List<(ulong, ulong)>();
		foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var bounds = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (bounds.Length is < 1 or > 2 ||
				!TryParseHex(bounds[0], out var start))
			{
				continue;
			}

			if (bounds.Length == 1)
			{
				ranges.Add((start, start + 1));
				continue;
			}

			if (TryParseHex(bounds[1], out var end) && end > start)
			{
				ranges.Add((start, end));
			}
		}

		return ranges.ToArray();
	}

	private static bool TryParseHex(string text, out ulong value)
	{
		var trimmed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
		return ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
	}

	private static bool IsWatchedFaultRip(ulong rip)
	{
		foreach (var (start, end) in _faultWatchRanges)
		{
			if (rip >= start && rip < end)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Records a fault that <paramref name="handler"/> resolved. Returns the
	/// value unchanged so call sites stay single expressions.
	/// </summary>
	private static bool RecordGuestFault(
		bool resolved,
		GuestFaultHandler handler,
		ulong rip,
		ulong rsp,
		ulong faultAddress)
	{
		if (!resolved || (!_faultCensusEnabled && _faultWatchRanges.Length == 0))
		{
			return resolved;
		}

		if (_faultCensusEnabled)
		{
			var counters = _faultCensus.GetOrAdd(rip, static _ => new long[GuestFaultHandlerCount]);
			Interlocked.Increment(ref counters[(int)handler]);
			Interlocked.Increment(ref _faultCensusTotal);
		}

		if (IsWatchedFaultRip(rip))
		{
			Console.Error.WriteLine(
				$"[LOADER][FAULT] rip=0x{rip:X16} handler={handler} " +
				$"fault_address=0x{faultAddress:X16} rsp=0x{rsp:X16} " +
				$"red_zone=0x{rsp - 128:X16}..0x{rsp:X16}");
		}

		return true;
	}

	private static unsafe ulong ReadFaultAddress(EXCEPTION_RECORD* exceptionRecord) =>
		exceptionRecord != null && exceptionRecord->NumberParameters >= 2
			? exceptionRecord->ExceptionInformation[1]
			: 0;

	private static void DumpGuestFaultCensus(string reason)
	{
		if (!_faultCensusEnabled)
		{
			return;
		}

		var total = Interlocked.Read(ref _faultCensusTotal);
		Console.Error.WriteLine(
			$"[LOADER][FAULT] census ({reason}): total={total} distinct_rip={_faultCensus.Count}");
		if (total == 0)
		{
			return;
		}

		var perHandler = new long[GuestFaultHandlerCount];
		foreach (var counters in _faultCensus.Values)
		{
			for (var index = 0; index < GuestFaultHandlerCount; index++)
			{
				perHandler[index] += Interlocked.Read(ref counters[index]);
			}
		}

		for (var index = 0; index < GuestFaultHandlerCount; index++)
		{
			if (perHandler[index] != 0)
			{
				Console.Error.WriteLine(
					$"[LOADER][FAULT]   {(GuestFaultHandler)index}={perHandler[index]}");
			}
		}

		// The red-zone question is about which guest code faults, so rank by RIP.
		var top = _faultCensus
			.Select(static entry =>
			{
				long total = 0;
				for (var index = 0; index < GuestFaultHandlerCount; index++)
				{
					total += Interlocked.Read(ref entry.Value[index]);
				}

				return (Rip: entry.Key, Count: total);
			})
			.OrderByDescending(static entry => entry.Count)
			.Take(30);
		foreach (var (faultRip, count) in top)
		{
			Console.Error.WriteLine($"[LOADER][FAULT]   rip=0x{faultRip:X16} faults={count}");
		}

		// The console dump is capped, but intersecting these RIPs with the
		// red-zone patcher's unprotected sites needs the whole set.
		var censusFile = Environment.GetEnvironmentVariable("SHARPEMU_FAULT_CENSUS_FILE");
		if (string.IsNullOrWhiteSpace(censusFile))
		{
			return;
		}

		try
		{
			var lines = new List<string>(_faultCensus.Count);
			foreach (var entry in _faultCensus)
			{
				for (var index = 0; index < GuestFaultHandlerCount; index++)
				{
					var count = Interlocked.Read(ref entry.Value[index]);
					if (count != 0)
					{
						lines.Add($"0x{entry.Key:X16},{(GuestFaultHandler)index},{count}");
					}
				}
			}

			File.WriteAllLines(censusFile, lines);
			Console.Error.WriteLine($"[LOADER][FAULT] census written to {censusFile} ({lines.Count} rows)");
		}
		catch (Exception error)
		{
			Console.Error.WriteLine($"[LOADER][WARN] could not write {censusFile}: {error.Message}");
		}
	}
}
