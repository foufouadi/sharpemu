<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Audit des PR KytyPS5 #720, #654, #558, #706, #497, #490, #638, #613, #512, #477

Ce document utilise les PR KytyPS5 comme **indices** de bugs réels, pas comme
modèle. Chaque correctif Kyty a été lu dans le diff réel (refs
`refs/pull/N/head` de `KytyPS5/KytyPS5`, diff calculé depuis le merge-base avec
`main`), puis confronté au chemin d'exécution équivalent de SharpEmu
(`claude/quirky-keller-epbw0y`, base `e2085ec`).

## Conventions

Niveau de preuve (colonne « Preuve ») :

- **S** : prouvé par analyse statique du code SharpEmu (chemin lu, invariant
  violé identifiable).
- **D** : comportement documenté (ISA RDNA2, spécification Vulkan/SPIR-V,
  PM4) ou implémenté de façon convergente par plusieurs compilateurs/pilotes de
  production (LLVM AMDGPU, Mesa ACO/radeonsi).
- **T** : prouvé par un test synthétique ajouté (voir commits).
- **H** : hypothèse ; plausible mais non démontrée pour SharpEmu.
- **NEEDS LOCAL VALIDATION** : l'effet sur un titre commercial (Astro Bot,
  Silent Hill, Ghost of Yōtei, Beast of Reincarnation, Uncharted, Demon's
  Souls) ne peut être confirmé qu'en exécutant le jeu. Aucun titre n'est
  déclaré « corrigé » dans ce document.

État SharpEmu : **OK** (déjà correct), **PARTIEL**, **INCORRECT**,
**ARCHI** (architecture différente rendant le correctif inutile), **ABSENT**.

Baseline tests (avant toute modification) : `SharpEmu.ShaderCompiler.Tests`
757/757 ; `SharpEmu.Libs.Tests` 3476 tests, 37 échecs préexistants, tous dans
des tests « device » exécutés sur lavapipe (llvmpipe, Vulkan CPU) :
DataShareSwizzle/ThreadRead/ThreadWrite, WaveLaneTransfer, GlobalDataShare,
`GpuBufferTests.IsInBounds_IsOverflowSafe`, `GpuRingBufferTests.Copy_*`,
`GuestBufferCacheTests.*Backing*`. Les tests dont le nom contient
`Multisample` font planter l'hôte de test sous lavapipe et sont exclus de la
baseline comme des runs suivants.

---

## 1. Synthèse

| # | Correctif SharpEmu proposé | Origine Kyty | État SharpEmu | Preuve | Décision |
|---|---|---|---|---|---|
| C1 | NOP PM4 « header-only » (`0xFFFF1000`) et `sceAgcCbNop` à 1 dword | #512 `501af4f3` | INCORRECT | S + D | Implémenter |
| C2 | `V_MAD_F32`/`V_MAC_F32`/`V_MADMK`/`V_MADAK` non fusionnés | #654 `ff425411` | INCORRECT | S + D | Implémenter |
| C3 | `DS_MIN_F32`/`DS_MAX_F32` : un seul opérande de données | #654 `015187e6` | INCORRECT | S + D | Implémenter |
| C4 | Images entières (Uint) → sampler point ; type de border color déduit de la vue | #512 `d8b942ad`, #497 `505190d2`, #477 (samplerCache) | INCORRECT | S + D | Implémenter |
| C5 | Readback d'image plus grande que l'anneau de download (32 MiB) | #638 `23110d00`, #654 `c560a345` | INCORRECT (Fatal) | S | Implémenter |
| C6 | `s_barrier` : ordonner aussi la mémoire buffer/image | #654 `375d8cc6` | PARTIEL | S + D | Implémenter |
| C7 | Formats sRGB étroits (R8/R8G8 sRGB) : décodage à l'échantillonnage | #706 | INCORRECT | S + D | Implémenter |
| C8 | Patch red-zone : protéger les instructions récupérées sur #UD | #720 (`redZonePatcher`) | INCORRECT | S | Implémenter |
| C9 | Émulation `MOVNTSS`/`MOVNTSD`/`CLZERO`/`RDPRU` sur hôte sans ces extensions | #720 | ABSENT | D | Implémenter |
| C10 | Initialiser à zéro page table BDA, bitset de fautes, null buffer | #654 `3b1c0c08` | INCORRECT | S + D | Implémenter |
| C11 | Âge GC des images compté en frames présentées, pas en soumissions | #654 `0653ab67` | INCORRECT | S | Implémenter si temps, NEEDS LOCAL VALIDATION |
| C12 | Dispatch compute dépassant `maxComputeWorkGroupCount` | #654 `467089d6`, #512 `98f94985` | INCORRECT | S + D | Reporté (conception : découpage via `vkCmdDispatchBase`) |
| C13 | Variantes `IMAGE_GATHER4_*` manquantes (L, B, CL, O…) | #613 `b3338fa7`, #654 `924e0980`, `ba405e6e` | PARTIEL | S | Reporté (sémantique LOD à établir) |
| C14 | Atomiques image 64 bits (DMASK=0x3) | #613 `f204e38a`, #654 `148cf1b8` | ABSENT | D | Reporté (dépend d'une extension, vue R64) |
| C15 | `DS_PERMUTE_B32` | #654 `015187e6` | ABSENT | D | Reporté |
| C16 | `SET_PREDICATION` op Z-pass (occlusion) | #558 `e9f211ef`, `7b71a647` | INCORRECT (Fatal) | S | Reporté (dépend du modèle d'occlusion) |
| C17 | Association stencil capturant une vue non-stencil | #613 `094bbf39`, #654 `41362cf2` | à confirmer | H | Reporté (test de reproduction d'abord) |

Tout le reste (voir §3) est déjà couvert, non applicable, spécifique à un titre
ou rejeté comme permissif.

---

## 2. Fiches détaillées des correctifs retenus

### C1 — NOP PM4 header-only

- **PR / commit Kyty** : #512 `501af4f3` « agc: support single-dword NOP packets ».
- **Problème réellement corrigé** : un paquet type-3 `IT_NOP` dont le champ
  COUNT vaut `0x3FFF` est un NOP d'un seul dword (en-tête seul). Mesa le définit
  explicitement (`PKT3_NOP_PAD = PKT3(PKT3_NOP, 0x3fff, 0)`, « header-only
  version »), ce qui donne l'en-tête `0xFFFF1000`.
- **Cause racine** : `PacketHeader.Length` applique la formule générique
  `COUNT + 2` ; pour `COUNT = 0x3FFF` elle rend 16385 dwords.
  `GpuCommandInterpreter` lève alors « packet is longer than the buffer » ou
  saute 64 KiB de commandes. Côté HLE, `sceAgcCbNop` refuse `dwordCount < 2`
  et renvoie `nullptr` alors que la bibliothèque doit pouvoir émettre ce NOP.
- **Sous-système** : processeur de commandes (PM4) + HLE Agc.
- **Kyty** : `graphicsRun.cpp::ProcessPm4`, `pm4.h::KYTY_PM4_LEN`, `agc.cpp::AgcCbNop`.
- **SharpEmu** : `Gpu/GpuCommands/PacketHeader.cs::Length`,
  `GpuCommandInterpreter.cs` (boucle de parsing, test `remaining < 2`),
  `Agc/AgcExports.CommandPackets.cs::CbNop`/`CbNopGetSize`.
- **État** : INCORRECT.
- **Dépendances** : aucune.
- **Applicabilité** : statique ; l'encodage est documenté par Mesa.
- **Approche Kyty** : cas particulier dans la boucle d'exécution et dans le
  dumper, plus un cas dans la macro de longueur.
- **Solution SharpEmu** : corriger l'invariant à un seul endroit,
  `PacketHeader.Length(header)`, que tous les consommateurs (interpréteur,
  scanner de paquets, dumps) utilisent déjà ; accepter un paquet d'un dword
  en fin de buffer ; `CbNop` accepte 1 dword et écrit `0xFFFF1000`.
- **Justification** : l'erreur est dans la définition de la longueur, pas dans
  un call site ; la corriger là couvre tous les chemins.
- **Effet attendu** : plus de désynchronisation du flux PM4 sur ce NOP.
- **Tests** : unitaire `PacketHeader.Length(0xFFFF1000) == 1` ; interpréteur
  exécutant `NOP-1dword` suivi d'un paquet valide et un NOP-1dword en dernière
  position ; `CbNop(1)`.
- **Difficulté** : faible. **Risque** : faible. **Validation runtime** : non
  requise pour la correction ; impact titre NEEDS LOCAL VALIDATION.

### C2 — `V_MAD_F32` et `V_MAC_F32` non fusionnés

- **PR / commit** : #654 `ff425411`.
- **Problème** : sur RDNA2, `V_MAD_F32`, `V_MAC_F32`, `V_MADMK_F32` et
  `V_MADAK_F32` arrondissent le produit avant l'addition ; seules les formes
  `V_FMA*` sont fusionnées. LLVM AMDGPU sélectionne ces opcodes pour le nœud
  `ISD::FMAD`, défini comme « multiply-add dont le résultat est celui des deux
  opérations arrondies séparément », et `V_FMA_F32`/`V_FMAC_F32` pour `fma`.
- **Cause racine** : `Gen5SpirvTranslator.Alu.cs` émet `GLSL.std.450 Fma`
  (ext 50) pour les deux familles. SPIR-V `Fma` est fusionné.
- **Sous-système** : traducteur SPIR-V (ALU vectorielle).
- **Kyty** : nouveaux opcodes IR séparés + décoration `NoContraction`.
- **SharpEmu** : `Gen5SpirvTranslator.Alu.cs` (`case "VMadF32"`, `"VMacF32"`,
  `"VMadMkF32"`, `"VMadAkF32"`). Le backend Metal (`Gen5MslTranslator.Alu.cs`)
  est vérifié séparément.
- **État** : INCORRECT (écart d'un ULP, et résultats différents quand la
  fusion évite une annulation catastrophique).
- **Dépendances** : aucune. `SpirvDecoration.NoContraction` existe déjà.
- **Approche Kyty** : opcode IR dédié. **SharpEmu** : pas d'IR intermédiaire ;
  il suffit d'émettre `OpFMul` puis `OpFAdd`, tous deux décorés
  `NoContraction` pour interdire au pilote de les refusionner.
- **Effet attendu** : résultats bit-exacts avec le GPU invité pour ces opcodes.
- **Tests** : test SPIR-V (plus de `Fma` pour `VMadF32`, présence de
  `NoContraction`) ; test device sur lavapipe avec des opérandes où
  `fma(a,b,c) != a*b+c` (ex. `a = 1+2^-12`, `b = 1-2^-12`, `c = -1`).
- **Difficulté** : faible. **Risque** : faible (plus conforme). La
  sémantique de dénormaux (flush) de `V_MAD_F32` n'est pas modélisée ; c'est
  inchangé par rapport à aujourd'hui.

### C3 — `DS_MIN_F32` / `DS_MAX_F32` à un seul opérande

- **PR / commit** : #654 `015187e6` (partie « single-operand ds_min/ds_max_f32 »).
- **Problème** : SharpEmu décode `DATA0` comme valeur de remplacement et
  `DATA1` comme opérande de comparaison (lecture littérale du pseudo-code de la
  doc ISA : `cmp = DATA2`). LLVM AMDGPU définit `ds_min_f32`/`ds_max_f32` avec
  un seul opérande de données (`DS_1A1D`) et s'en sert pour `atomicrmw fmin/fmax`
  en LDS ; Mesa ACO fait de même pour `shared_atomic_fmin/fmax` et RADV expose
  `shaderSharedFloat32AtomicMinMax` sur RDNA2 (couvert par le CTS). Kyty
  observe en plus que le compilateur Sony laisse `DATA1 = 0` (donc `v0`).
  Trois sources indépendantes convergent : le matériel compare `DATA0` à la
  mémoire.
- **Cause racine** : `Gen5ShaderTranslator.cs` (sources de `DsMinF32`) et
  `EmitDataShareFloatAtomic(pointer, data, compare, …)` qui compare avec `v[DATA1]`.
- **SharpEmu** : `Gen5ShaderTranslator.cs` (liste des sources DS),
  `Gen5SpirvTranslator.cs` (LDS), `Gen5SpirvTranslator.Resources.cs` (GDS),
  test `Gen5ShaderAtomicDecodeTests.DsFloatMinMax_*` qui fige l'ancienne lecture.
- **État** : INCORRECT (min/max calculé contre un registre sans rapport, souvent l'adresse).
- **Conflit documentaire** : la doc ISA dit le contraire ; la décision repose
  sur deux compilateurs de production qui passent la conformance. Le test
  existant est mis à jour en conséquence.
- **Solution** : une seule source de données ; la comparaison utilise la même
  valeur que le remplacement.
- **Tests** : décodage (2 sources) ; SPIR-V ; test device : LDS initialisée, `DATA1`
  pointant vers un registre différent, vérifier que `min(mem, v[DATA0])` est écrit.
- **Difficulté** : faible. **Risque** : faible.

### C4 — Échantillonnage des images entières

- **PR / commits** : #512 `d8b942ad` et #497 `505190d2` (point filtering pour
  images entières, deux PR indépendantes), #477 (`samplerCache.cpp`, border
  color flottante pour les samplers de comparaison).
- **Problème A (filtrage)** : Vulkan interdit `VK_FILTER_LINEAR` (et le
  mipmap linéaire) sur un format sans `SAMPLED_IMAGE_FILTER_LINEAR`, ce qui
  exclut tous les formats entiers. `ResourceMaterializer.RequiresPointSampler`
  ne force un sampler point que pour `Sint` et les formats convertis, pas pour
  `Uint`.
- **Problème B (border color)** : `SamplerStore` crée toujours des border
  colors `VK_BORDER_COLOR_INT_*`. La spec Vulkan (Texel Replacement) exige que
  le type de border color corresponde au type numérique de la vue : `FLOAT_*`
  pour UNORM/SNORM/FLOAT/depth, `INT_*` pour les formats entiers ; sinon la
  valeur est indéfinie. C'est le cas le plus courant (textures flottantes en
  `ClampToBorder`, shadow maps en blanc opaque).
- **Cause racine** : le sampler hôte est dérivé des seuls mots du S#, alors que
  deux de ses propriétés dépendent de l'image appariée. SharpEmu possède déjà
  le mécanisme adéquat : `BuildSamplerPlan` clone un sampler quand ses usages
  sont incompatibles (point forcé, comparaison).
- **SharpEmu** : `ShaderCompiler/Resources/ResourceMaterializer.cs`
  (`RequiresPointSampler`, `BuildSamplerPlan`), `ShaderResourceInfo.cs`
  (`SamplerResource`), `VulkanVideoPresenter.Descriptors.cs::ResolveSampler`,
  `Gpu/Images/SamplerStore.cs`, backend Metal
  (`MetalCommandStreamHost.Rendering.cs`) pour la cohérence.
- **État** : INCORRECT.
- **Approche Kyty** : (A) ajout de `Uint` au test ; (B) float seulement pour
  les samplers de comparaison.
- **Solution SharpEmu** : (A) `RequiresPointSampler` couvre tous les types
  numériques entiers. (B) nouvelle propriété de plan `IntegerBorder` sur
  `SamplerResource`, calculée par la même passe que le point forcé (un sampler
  apparié à une vue entière — `Uint`, `Sint` ou format converti lu en entier —
  reçoit un border entier, les autres un border flottant ; usage mixte =
  clonage comme pour la comparaison). `SamplerStore` choisit `FLOAT_*`/`INT_*`
  à partir de ce drapeau et l'inclut dans sa clé de cache. C'est plus général
  que Kyty (qui ne corrige que la comparaison) et déterministe.
- **Tests** : unitaires sur le plan (sampler apparié à Uint → point + integer
  border ; à Float → float border ; mixte → deux samplers) ; unitaire
  `SamplerStore` (mapping border) ; test device possible (échantillonnage hors
  bornes d'une texture R32F en `ClampToBorder`/blanc opaque doit donner 1.0).
- **Difficulté** : moyenne. **Risque** : faible à moyen (plus de samplers
  distincts). Effet titre NEEDS LOCAL VALIDATION.

### C5 — Readback d'image au-delà de l'anneau de download

- **PR / commits** : #638 `23110d00` (tampon temporaire), #654 `c560a345`
  (découpage en lots).
- **Problème** : `GuestImageCache.TryDownloadToGuest` mappe toute l'image dans
  l'anneau `Download` de 32 MiB et lève un Fatal si ça ne tient pas
  (ex. cible 3840×2160 RGBA16F ≈ 66 MiB). Le chemin buffer, lui, découpe déjà
  (`BufferDownloadBatchPlanner`).
- **Cause racine** : la capacité de l'anneau est traitée comme une limite de
  taille d'image.
- **SharpEmu** : `Gpu/Images/GuestImageCache.Transfers.cs::TryDownloadToGuest`,
  `Gpu/Buffers/GuestBufferCache.cs` (anneau 32 MiB), modèle déjà existant côté
  upload : `GuestBufferUploader` crée un `GpuBuffer` temporaire libéré par
  `QueueCompletionAction`.
- **État** : INCORRECT (Fatal).
- **Approche Kyty** : #638 tampon temporaire ; #654 découpage par niveaux,
  couches et lignes avec attente entre lots (≈700 lignes).
- **Solution SharpEmu** : garder l'anneau comme chemin rapide ; si la
  réservation échoue, utiliser un `GpuBuffer` `Download` dédié de la taille
  exacte, relâché après la publication (même motif que l'upload). Pas de
  découpage : le plan de copie image→buffer (`DownloadToBuffer`, tiler) reste
  intact et un seul chemin de publication est conservé.
- **Tests** : test de cache (lavapipe) avec une image > 32 MiB marquée écrite
  par le GPU, readback forcé, contenu vérifié en mémoire invitée.
- **Difficulté** : faible. **Risque** : faible (allocation ponctuelle).

### C6 — Sémantique mémoire de `s_barrier`

- **PR / commit** : #654 `375d8cc6`.
- **Problème** : SharpEmu émet `OpControlBarrier(Workgroup, Workgroup,
  AcquireRelease|WorkgroupMemory)`. Le motif invité
  `store buffer ; s_waitcnt vmcnt(0) ; s_barrier ; load buffer` (données
  partagées par la mémoire globale au sein d'un workgroup) n'est pas ordonné
  par le modèle mémoire Vulkan sans `UniformMemory`/`ImageMemory`.
- **Cause racine** : `s_waitcnt` est traduit comme no-op et la barrière ne
  couvre que la LDS.
- **SharpEmu** : `Gen5SpirvTranslator.cs` (`SBarrier`).
- **Solution** : sémantique `AcquireRelease | WorkgroupMemory | UniformMemory |
  ImageMemory` (0x948). Aucun cas particulier.
- **Tests** : test SPIR-V sur la constante de sémantique.
- **Difficulté** : faible. **Risque** : faible (plus strict ; coût perf
  potentiel). Validation runtime : le bénéfice n'est observable que sur un GPU
  réel — NEEDS LOCAL VALIDATION pour l'effet titre.

### C7 — Formats sRGB étroits (`Bits8Srgb`, `Bits8_8Srgb`)

- **PR** : #706.
- **Problème** : `GuestPixelFormats` stocke `Bits8Srgb`→`R8Unorm` et
  `Bits8_8Srgb`→`R8G8Unorm` (formats sRGB étroits optionnels en Vulkan). Aucun
  code ne décode le sRGB : un échantillonnage renvoie les valeurs encodées
  gamma comme si elles étaient linéaires.
- **Cause racine** : la substitution de format perd la fonction de transfert.
- **SharpEmu** : `Gpu/Images/GuestPixelFormats.cs`, `TextureTransferLayout.SurfaceFormat`,
  `ImageRequestBuilders.Texture.cs`, `ViewFormatRules`, `CachedImage.CreateFlags`
  (images `MUTABLE_FORMAT`).
- **État** : INCORRECT.
- **Approche Kyty** : drapeau `srgb_sample_decode` dans la spécialisation +
  décodage après échantillonnage dans le shader.
- **Options SharpEmu** :
  - B (adaptation minimale) : même décodage shader que Kyty. Défaut : le
    décodage après filtrage bilinéaire est faux (on filtre des valeurs gamma).
  - C (native) : les images étant `MUTABLE_FORMAT`, créer la **vue**
    d'échantillonnage en `R8_SRGB`/`R8G8_SRGB` quand le périphérique annonce
    `SAMPLED_IMAGE` pour ce format ; le matériel décode avant filtrage, sans
    permutation de shader. Le stockage reste R8/R8G8 UNORM, donc aucun impact
    sur l'aliasing, les copies ou le tiler.
- **Choix** : C, avec vérification des features du format. Si le périphérique
  ne supporte pas la vue sRGB, le comportement actuel est conservé et un
  avertissement unique est journalisé (pas de faux succès silencieux).
- **Hors périmètre** : l'encodage sRGB en écriture de cible couleur
  `Bits8Srgb` (même principe, vue d'attachement sRGB) ; noté, non traité ici.
- **Tests** : unitaires sur la sélection de format de vue (support présent /
  absent) ; test device possible : texture R8 sRGB contenant 0x80, lecture
  ≈ 0.2158.
- **Difficulté** : moyenne. **Risque** : faible. Effet titre NEEDS LOCAL VALIDATION.

### C8 — Patch red-zone et instructions récupérées sur #UD

- **PR** : #720 (`redZonePatcher.cpp::MayEmulateInstruction`).
- **Problème** : sous Windows (et macOS), le noyau pousse le contexte
  d'exception sous `RSP`, écrasant la red zone SysV de 128 octets de la
  fonction invitée. `GuestRedZonePatcher` protège les instructions
  susceptibles de fauter (`IsFaultableGuestMemoryInstruction`), mais seulement
  celles qui accèdent à la mémoire. Or `DirectExecutionBackend` récupère aussi
  des #UD sur des instructions **registre-seul** quand l'hôte n'a pas
  l'extension : `EXTRQ`/`INSERTQ` (SSE4a, absent chez Intel),
  `MONITORX`/`MWAITX`, BMI1/BMI2/ABM. Une telle instruction dans une fonction
  qui utilise sa red zone corrompt ses variables locales à chaque exécution.
- **Cause racine** : le patcher et le gestionnaire #UD ont deux notions
  différentes de « peut fauter ».
- **SharpEmu** : `Core/Loader/GuestRedZonePatcher.cs`,
  `Core/Cpu/Native/DirectExecutionBackend.Amd64Compat.cs`,
  `DirectExecutionBackend.IllegalInstruction.cs`.
- **Approche Kyty** : liste statique de mnémoniques.
- **Solution SharpEmu** : prédicat unique « l'hôte peut lever #UD sur cette
  instruction » dérivé des features CPUID requises par l'instruction (Iced
  `Instruction.CpuidFeatures()`) et des features réellement présentes sur
  l'hôte (CPUID lu une fois). Le patcher traite ces instructions comme
  faultables. Sur un hôte qui possède les extensions, rien ne change (pas de
  patch inutile, contrairement à une liste statique).
- **Tests** : unitaires sur le prédicat (avec un jeu de features hôte
  injecté) et sur la collecte de sites d'une fonction red-zone contenant
  `extrq xmm0, xmm1`.
- **Difficulté** : moyenne. **Risque** : moyen (plus de sites patchés sur
  hôtes Intel). NEEDS LOCAL VALIDATION sur un hôte Intel Windows.

### C9 — `MOVNTSS`/`MOVNTSD`, `CLZERO`, `RDPRU`

- **PR** : #720 (`x64InstructionEmulator.cpp`).
- **Problème** : ces instructions Zen2 lèvent #UD sur les hôtes qui ne les
  ont pas (Intel pour les trois familles). SharpEmu n'a pas de repli.
- **Sémantique documentée (AMD APM)** : `MOVNTSS/MOVNTSD m, xmm` stocke les
  32/64 bits bas ; `CLZERO` met à zéro la ligne de 64 octets contenant `RAX` ;
  `RDPRU` lit `MPERF` (ECX=0) ou `APERF` (ECX=1) dans `EDX:EAX`.
- **Solution SharpEmu** : helpers purs dans `Core/Cpu/Emulation` (calcul testable),
  branchés sur le chemin #UD existant de `DirectExecutionBackend.Amd64Compat`.
  `RDPRU` renvoie un compteur monotone de l'hôte pour les deux sélecteurs
  (le ratio APERF/MPERF vaut 1 — approximation explicite) ; tout autre sélecteur
  renvoie 0.
- **Hypothèse** : la façon exacte dont `RDPRU` positionne `CF` pour un
  sélecteur invalide n'est pas vérifiée ; elle n'est pas modélisée.
- **Tests** : unitaires sur les helpers (adresse de ligne `CLZERO`, stockage
  partiel `MOVNTSS`, sélection `RDPRU`).
- **Difficulté** : moyenne. **Risque** : faible (n'intervient qu'après un
  #UD qui aurait sinon arrêté le titre). NEEDS LOCAL VALIDATION sur Intel.

### C10 — Buffers device lus avant toute écriture

- **PR / commit** : #654 `3b1c0c08`.
- **Problème** : la mémoire d'une allocation Vulkan est indéfinie. Dans
  `GuestBufferCache`, la page table BDA (`_bdaPageTable`, lue par chaque
  accès mémoire physique des shaders), le bitset de fautes de
  `BdaFaultProcessor` et le null buffer ne sont jamais initialisés ; seuls les
  intervalles enregistrés/désenregistrés sont écrits.
- **Cause racine** : les constructeurs n'ont pas de command buffer actif.
- **Approche Kyty** : drapeau + `EnsureDeviceStateCleared()` à chaque accesseur.
- **Solution SharpEmu** : un point unique : le cache enregistre ces
  initialisations au premier command buffer (au moment où le scheduler
  devient actif) avant toute autre commande ; aucun test dans les accesseurs.
- **Tests** : test cache (lavapipe) : lecture de la page table et du null
  buffer après création ; la preuve principale reste statique (lavapipe met
  souvent la mémoire à zéro).
- **Difficulté** : faible. **Risque** : faible.

### C11 — Âge des images en frames

- **PR / commit** : #654 `0653ab67`.
- **Problème** : `GuestImageCache.RunGarbageCollector` incrémente
  `_collectionTick` à chaque appel ; il est appelé à la fin de **chaque
  soumission** (`CommandStreamQueue`) et à chaque flip. L'âge 16/80/160 est
  donc exprimé en soumissions. Avec `_collectionStartBytes = 0`, la collecte
  tourne toujours, et une image GPU-modifiée non relisible
  (`CanReadBack == false`) est supprimée sans pression mémoire.
- **Solution** : l'horloge de récence n'avance qu'une fois par frame
  présentée ; la collecte peut continuer à s'exécuter plus souvent.
- **Tests** : unitaire sur l'âge (plusieurs collectes sans flip ne rendent
  pas une image candidate).
- **Difficulté** : faible. **Risque** : moyen (rétention mémoire plus longue).
  NEEDS LOCAL VALIDATION.

---

## 3. Correctifs examinés et non retenus

### #490 — `IMAGE_ATOMIC_FMIN/FMAX`

| Correctif | État SharpEmu | Justification |
|---|---|---|
| Décodage MIMG 0x1E/0x1F | OK | `Gen5ShaderTranslator.cs` (0x1E/0x1F) |
| Traduction par boucle CAS | OK | `Gen5SpirvTranslator.cs` (`ImageAtomicFmax/Fmin`) ; sémantique NaN/±0 identique à Kyty (garde l'ancienne valeur) |
| Vue R32Uint pour atomiques flottants | OK | `ImageRequestBuilders.Texture.cs` (`shape.Atomic` → `R32Uint`) |

### #706 — voir C7 (retenu, conçu différemment).

### #720 — Intel/Astro Bot

| Correctif | État | Décision |
|---|---|---|
| Émulation CLZERO/RDPRU/MOVNTSS/MOVNTSD | ABSENT | C9 |
| Red-zone pour instructions émulées | INCORRECT | C8 |
| SHA-NI (`SHA1*`, `SHA256*`) | ABSENT | Non retenu dans ce lot : présent sur la plupart des CPU Intel récents ; à traiter si un hôte le requiert |
| `HostMemoryQueryRange` macOS | ARCHI | SharpEmu lit la mémoire invitée via `ctx.Memory.TryRead` |
| Validation du pointeur PCM, verrous AudioOut2 | ARCHI | `AudioOut2Exports` lit via accès vérifiés et `ConcurrentDictionary` ; pas d'use-after-free en C# managé |
| `memset` de l'arène de `AudioOut2ContextCreate` | Rejeté | Aucune preuve que la bibliothèque réelle initialise l'arène ; masque probablement un autre bug |

### #638 — pression mémoire

| Correctif | Décision |
|---|---|
| Readback > anneau | C5 |
| GC à la demande sur miss d'image | Reporté : dépend d'une mesure de budget mémoire que SharpEmu n'a pas (comptabilité interne seulement) ; NEEDS LOCAL VALIDATION |
| Hystérésis / téléchargement des images tilées en mode agressif | Rejeté : réglage heuristique, non démontrable sans runtime |

### #477 — Demon's Souls

| Correctif | Décision |
|---|---|
| Border color flottante pour samplers de comparaison | Généralisé en C4 |
| `DepthFatal` → `return`/format de repli, suppression des bornes du tiler, extension silencieuse de `linear_capacity` | Rejeté : remplace des erreurs par du silence ou masque une incohérence de layout |
| `stencil_clear |= depth_meta_clear_enable`, masques RT « write mask » | Rejeté : non justifié par une sémantique documentée, dépend du modèle d'état Kyty |
| Suivi `target_channel_type/format` dans les registres shader | ARCHI : SharpEmu dérive l'export des cibles résolues |
| Refonte pipeline/descripteurs indirects, UI launcher, audio | Non applicable (architecture Kyty, dérive de base) |

### #613

| Correctif | État | Décision |
|---|---|---|
| ttmp, `S_SWAPPC_B64`, `S_CODE_END`, `S_CBRANCH_CDBG*` | OK | Déjà décodés |
| `IMAGE_GATHER4_L` exact | PARTIEL | C13 reporté |
| Atomiques image 64 bits | ABSENT | C14 reporté |
| Mesh en wave32 | à vérifier | Non traité (chemin mesh SharpEmu distinct) |
| Association stencil limitée aux vues stencil | à confirmer | C17 reporté |
| Surfaces BC comme vues storage | Non retenu : dépend de `maintenance6` et du modèle de vues bloc |
| Swapchain sRGB | OK | `VulkanVideoPresenter.Present.cs` choisit déjà la paire UNORM/SRGB |
| Canaux AAC, patch EOP Agc, cmask fast clear, sondes audio | Non traités : sous-systèmes HLE distincts, preuves spécifiques aux titres |

### #654

| Correctif | État | Décision |
|---|---|---|
| `V_MAD/MAC_F32` non fusionnés | INCORRECT | C2 |
| `DS_MIN/MAX_F32` un opérande | INCORRECT | C3 |
| `DS_PERMUTE_B32` | ABSENT | C15 reporté |
| `DS_MSKOR_B32` | OK | Déjà traduit |
| `V_ALIGNBYTE_B32` (octet via `S2[1:0]`) | OK | `Gen5SpirvTranslator.Alu.cs` masque déjà `& 3` |
| Stores de buffer formatés (UNORM/SNORM/half) | OK | `EncodeGfx10BufferComponent` |
| `s_barrier` mémoire buffer | PARTIEL | C6 |
| Init page table/fault/null | INCORRECT | C10 |
| Âge GC en frames | INCORRECT | C11 |
| Découpage des downloads | INCORRECT | C5 (autre conception) |
| Limites de dispatch | INCORRECT | C12 reporté (découpage fidèle plutôt que suppression) |
| VOPC SDWA, `V_CMPX_*16` | OK | Tables VOPC SharpEmu complètes pour ces opcodes |
| Toutes variantes gather4, gathers LOD explicite | PARTIEL | C13 reporté |
| B10G11R11/E5B9G9R9 hors classe 32 bits | Rejeté | Dans la spec Vulkan ces formats appartiennent bien à la classe de compatibilité 32 bits ; le symptôme Kyty relève de leur cache |
| Négation lane 16 bits entière par bit de signe | H | Non retenu : sémantique `neg` sur VOP3P entier non établie |
| Pipeline cache disque, capacités 16 bits, NMin/NMax, casts | Non retenu : performance / infrastructure Kyty |
| Red-zone RBP + balayage des fonctions hors `.eh_frame` | Reporté : pertinent pour C8 mais demande un désassemblage de segment complet |
| Relecture des images linéaires par défaut | ARCHI | SharpEmu synchronise les lectures CPU via la garde de pages (`TrySynchronizeCpuRead`) |
| Structuration CFG UE5, SRT non évaluables | ARCHI | Pipeline de ressources SharpEmu différent (`ResourceMaterializer`, tables de candidats) |
| Entitlement key | Non traité (HLE Np, preuve titre) |

### #512

| Correctif | État | Décision |
|---|---|---|
| NOP PM4 1 dword | INCORRECT | C1 |
| Point filtering images Uint | INCORRECT | C4 |
| Chaînage IB (bit 20) | OK | `GpuCommandInterpreter.Adaptations.cs` (`ChainToBuffer`) |
| Prédicat conditionnel aligné dword | à vérifier | Non traité dans ce lot |
| Snapshot des arguments de dispatch indirect, paires de registres, labels de release | ARCHI | Interpréteur SharpEmu à lecture vérifiée ; pas de preuve d'incohérence |
| Stockage des paquets soumis possédé par la soumission | à vérifier | `CommandSubmission` relit les paquets en mémoire invitée à l'exécution (comme le CP réel) ; aucune preuve qu'un appel HLE passe un tableau temporaire. Non traité |
| `V_CMPX_EQ_U16/U64`, `V_NOT_B32` SDWA | OK | Décodés |
| Tables de descripteurs indexées GPU, PHI, LDS privée, radix sort | ARCHI | Spécifique au planificateur de ressources Kyty |
| Vdecsw, savedata, swapchain minimisée, stencil reference | Non traités (HLE / présentation, hors lot) |

### #558 — Astro Bot

| Correctif | Décision |
|---|---|
| Plafonds de taille de shader, drops par `TITLE_ID == PPSA21564`, bail-out WAIT_REG_MEM après 4 s, zéro des dwords S# invalides | Rejeté : spécifiques au titre ou permissifs |
| Z-pass predication « toujours visible » | Rejeté comme contournement ; C16 reporté (vraie sémantique) |
| Détacher la cible depth de taille différente | Rejeté : change la sémantique (le HW clippe chaque attachement) |
| JSON avec commentaires / virgules finales | H : aucune preuve que le parseur PS5 les accepte |
| `S_WQM_B32`, `S_*_SAVEEXEC_B32` | OK | Déjà décodés |
| BVH 0xE6/0xE7 | OK | Déjà décodés |
| Null image depth pour `Dref` | à vérifier | `GetNullImage` est indexé par format ; non traité dans ce lot |
| DPP et lanes helper | à vérifier | Non traité dans ce lot |
| PRX optionnel invalide | Non traité (loader, preuve titre) |

### #497 — Ghost of Yōtei

La quasi-totalité (ordonnanceur « cooperative », spills, compaction SPIR-V,
alias SRT) dépend de l'architecture de dispatch de Kyty : ARCHI. Seule
convergence retenue : filtrage point des images entières (→ C4).
`V_CMPX_NE_U16` et CMPX signés 16 bits : OK (déjà décodés).

---

## 4. Ordre d'implémentation

C1, C2, C3, C4, C5, C6, C7, C10, C8, C9, puis C11 si le temps le permet. Un
commit par correctif, chacun avec son test de régression, en expliquant
l'invariant corrigé et non le titre qui a motivé la recherche.
