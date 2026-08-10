# Ghost of Yōtei (PPSA26344, v01.008.000) — investigation

État au 2026-08-08. Non suivi par git (`.gitignore`, même convention que
`PROGRESS.md`/`HIZ_INVESTIGATION.md`). Document de référence unique du dossier :
les constats vont ici, pas en dump de log dans le chat.

EBOOT : `C:\Users\foufouadi\Documents\jeux\ghost\eboot.bin`
Repo : `~/Documents/code/sharpemu`, branche `main` @ `fa6a9d2`
Référence de comparaison : Kyty, `~/Documents/code/KytyPS5` (C++, GPL-2.0)
Lancement : PowerShell (`$env:VAR="..."`, backtick de continuation,
`Start-Process -RedirectStandardError`). Un `\` de continuation bash casse
l'appel — l'exe reçoit `C:\` comme argument.

---

## 1. Règle méthodologique impérative

**`SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES=1` ne doit jamais être actif pendant
un diagnostic.**

Ce flag ne se contente pas de forcer des soumissions : il **bascule la
sémantique de comparaison des attentes GPU**. `_equalCompareExact`
(`GpuWaitRegistry.cs:658-666`) vaut `false` uniquement quand il est actif,
transformant `cmp=3` de `==` en `>=`. Deux sémantiques différentes selon un
flag de debug.

Effet mesuré sur un run : 7 018 `orphan_preamble_force_submit`,
1 010 `fence_write_salvage`, et **zéro** `write_data`/`release_mem` normal.
Le pipeline observé était intégralement artificiel.

Conséquence : tous les logs `debug/yotei_hizwatch_*` sont inexploitables pour
un diagnostic. Les runs de référence sont `yotei_noorphan_20260808_175405` et
`yotei_puremain_20260808_181154`.

**`SHARPEMU_LOG_AGC=1` est obligatoire** pour tracer `write_data`/`release_mem` :
`TraceAgc` est gardé par `_traceAgc` (`AgcExports.cs:2232`). Sans ce flag,
leur absence dans un log ne prouve rien.

---

## 2. Cause racine établie

### Le fait central

Run de contrôle sans aucune béquille, sur `main` pur (build depuis un arbre
`git stash`é), `debug/yotei_puremain_20260808_181154_stderr.log` :

| Compteur | Valeur |
|---|---|
| `agc.cb_release_mem` (paquets **construits**) | 83 |
| `agc.dcb.release_mem` (paquets **exécutés**) | 10 |
| `driver_submit_dcb_call` | 1 |
| `driver_submit_acb_call` | 6 |
| `agc.wait_suspended` | 6 (3 résolus, 3 jamais) |
| `presented guest frame` | 1 |

Chiffres **identiques au bit près** entre `main` pur et `main` + patches
locaux (`yotei_noorphan_20260808_175405`). Le comportement est donc celui de
`main`, indépendant de toute modification locale.

**88 % du travail GPU construit par le jeu n'est jamais soumis au parser.**

### Le mécanisme

Quand une arène de commandes est pleine, SharpEmu rappelle le jeu via un
callback pour qu'il en fournisse une nouvelle
(`AgcExports.cs`, `TryAllocateCommandDwords`). Sur 89 callbacks :

| Résultat | Nombre |
|---|---|
| `result=0x0` | **85** |
| non nul | 4 |

Les 4 valeurs non nulles retournées : `0x201D540000`, `0x201E960200`,
`0x201EBA6300`, `0x201E970300`. Les adresses ACB effectivement soumises :
`0x201E960200`, `0x201EBA6300`, `0x201E970300`. **Corrélation totale** — seuls
les buffers dont le callback a répondu non-zéro sont soumis. Les 3 autres
soumissions viennent d'arènes initiales, jamais rechargées.

### Ce que Kyty établit

`src/libs/agc.cpp:226` :

```cpp
using Callback = KYTY_SYSV_ABI bool (*)(CommandBuffer*, uint32_t, void*);
```

Le retour est un **`bool`**, pas une adresse. Le callback modifie le
`CommandBuffer` en place (`cursor_up`/`cursor_down`) ; Kyty relit ensuite
`GetAvailableSizeDW()` pour vérifier (`agc.cpp:302`).

Donc **`result=0` signifie : le jeu refuse explicitement d'agrandir l'arène.**

Le layout de `CommandBuffer` est identique des deux côtés —
`bottom/top/cursor_up/cursor_down/callback/user_data/reserved_dw` aux offsets
`0x00/0x08/0x10/0x18/0x20/0x28/0x30`, cf. `AgcExports.cs:2146-2150`. Ni
problème de structure, ni de convention d'appel.

### Les 3 labels réellement bloqués

`0x2011831650`, `0x2011669FE0`, `0x2011882330` — profil identique pour les
trois : `cb_release_mem` construit, aucun `dcb.release_mem`, producteur situé
dans un buffer absent de la liste des soumissions.

Exemple (`0x2011831650`) :

```
t=3.780  agc.cb_release_mem  buf=0x804F70710 cmd=0x20118249F0 dst=0x2011831650
t=4.779  agc.dcb.wait_reg_mem addr=0x2011831650 value=0x0 ref=0x1 satisfied=False
t=4.781  agc.wait_suspended ... producer=none-observed; remaining-suspended
```

Contraste avec les 3 attentes qui se résolvent (`0x201162C080`,
`0x201162C278`, `0x2000000020`) : elles reçoivent bien leur
`agc.dcb.release_mem ... wrote=True` et reprennent (65 ms et 1,6 ms).

**Quand un paquet est parsé, il fonctionne** : `wrote=True` sur les 10
exécutés, zéro échec d'écriture. Le bug n'est pas dans le parser PM4.

---

## 3. Pistes réfutées — ne pas y revenir

| Piste | Réfutation |
|---|---|
| `EVENT_WRITE_EOP` (opcode `0x47`) manquant | 0 occurrence sur 623 778 lignes. Le logger d'opcodes inconnus ne remonte qu'un `op=0x00` (padding). Tous les opcodes utilisés par Yotei sont reconnus. |
| Décodage `data_sel` cassé | Écart réel avec Kyty (SharpEmu lit bits 16-23 dans la forme AGC-nop, Kyty bits 29-31) mais **Yotei ne l'exerce pas** : seules les valeurs 1/2/3 apparaissent (45/42/6), toutes gérées, toutes `wrote=True`. Bug dormant, à corriger séparément. |
| Label `0x201452C064` « sans producteur » | **Adresse fantôme.** Présente dans 2 runs (34 et 28 occurrences), **absente d'un 3ᵉ** (0 occurrence, 0 attente sur `dcb.graphics`). L'allocateur rend une adresse différente à chaque lancement. Toute théorie bâtie dessus est nulle. |
| `func=4` = « Not Equal » | Faux. `cmp=3` sur 100 % des attentes bloquées (46/46/42 occurrences, `cmp=4` : zéro). `GpuWaitRegistry.cs:672-684` donne `3 => masked >= reference`. |
| « `flip_version` 19→67 = progrès » | `presented guest frame` reste à **1** avant comme après. Progrès de plomberie interne, pas d'affichage. Mesuré sous béquille, donc non représentatif. |

---

## 4. Correctifs dans l'arbre de travail (non commités)

### 4.1 `PhysicalVirtualMemory.TryRead`/`TryCompare`/`TryCopy` — bug générique

Une lecture sur page `PAGE_GUARD` levait `STATUS_GUARD_PAGE_VIOLATION` en
exception matérielle non rattrapable → `FailFast` du process, au lieu de
renvoyer `false`. Déclenché par `DynamicHiZWatch` armant un piège que
`ProbeTexture`/`TryCreateGuestDrawTexture` venaient déclencher eux-mêmes.

Fix en trois couches : délégué `DynamicHiZGuardPageRangeCheck` (`AgcExports.cs`),
`RangeOverlapsDynamicHiZGuardPage` passé `internal`
(`DirectExecutionBackend.cs`), et vérification centralisée dans
`PhysicalVirtualMemory` couvrant tous les appelants.

**Bug de robustesse indépendant de Yotei — candidat au commit et à la remontée
amont.**

### 4.2 Diagnostic d'allocation d'arène (`TryAllocateCommandDwords`)

Aligné sur les trois cas distincts de Kyty, tous logués inconditionnellement :

| Cas | Avant | Après |
|---|---|---|
| pas de callback | `return false` muet | `agc.cmd_alloc_no_callback` |
| callback renvoie `false` | **traité comme un succès** | `agc.cmd_alloc_callback_refused` |
| callback OK mais place insuffisante | trace opt-in | `agc.cmd_alloc_callback_no_space` |

Le code ne testait que la valeur de retour de `TryCallGuestFunction`
(« l'appel a-t-il eu lieu ») et ignorait `callbackResult` (« le jeu a-t-il
réussi »). Un refus explicite du guest passait pour un succès, puis échouait
plus loin sans un mot.

Build OK, 874 tests verts, vérification structurelle ad-hoc 15/15.
**C'est du diagnostic : ça ne débloque rien.**
Binaire : `artifacts\publish\alloc-diag\SharpEmu.exe`.

### 4.3 Hack spin-flag (`SHARPEMU_FORCE_SPIN_FLAG_RIP`)

Dans `DirectExecutionBackend.cs`, garde de stabilité RIP+RSP sur 3 ticks,
re-armable. **N'a tiré 0 fois** sur le run de référence : le jeu passe la
cinématique sans. À réévaluer ultérieurement — possiblement inutile désormais.

### 4.4 FIX DIFFÉRÉ — politique d'attente de queue audio, à reprendre de Kyty

**Statut : non implémenté, à faire plus tard. Investigation Yotei en cours
ailleurs (FaWorkerIo1, §5) ; ce fix est une robustesse générale, pas la cause
du blocage ici.**

Piste ouverte par lecture comparative de `KytyPS5/src/libs/audio.cpp:371-385`
vs `SharpEmu.HLE/Host/Sdl/SdlHostAudio.cs:201-257` (2026-08-08, fin de
session — voir note de passation). Le modèle d'exécution est identique
(soumission synchrone depuis le thread invité, aucun thread audio dédié),
mais la **politique d'attente de queue** diffère :

| | SharpEmu `Submit` | Kyty `QueueSdlAudio` |
|---|---|---|
| Seuil d'attente | `_maximumQueuedBytes` (~683 ms cap), boucle `Thread.Sleep(1)` | `target_latency_us=40000` (~40 ms), ~16x plus court |
| À saturation | boucle `Sleep(1)` jusqu'à 250 ms puis enqueue quand même (`overrun=true; break`) | `SleepMicro(1000)` jusqu'à 200 ms puis `SDL_ClearQueuedAudio` + `break` |
| File vidée en cas de blocage | **non** (empile au-delà du cap) | **oui** (purge) |

**Pourquoi c'est intéressant** : la boucle `Sleep(1)` serrée de SharpEmu est
structurellement capable de monopoliser le thread invité exactement comme le
décrit le marasme ScreamWorker des §7quater–7duodecies (adresse figée dans
`ntdll`, des centaines de milliers d'appels `scePthreadMutexLock`/s). L'équivalent
Kyty a une parade (timeout + purge) qui évite cette monopolisation.

**Pourquoi ce N'EST PAS la cause du run étudié** : `SdlHostAudio.Submit` a été
mesuré `blocked=0%` sur 2 minutes complètes (`SHARPEMU_LOG_AUDIO_QUEUE=1`,
§7novies) — la contre-pression ne se déclenche jamais dans ce run précis.
Donc ce fix n'explique pas le marasme observé ici. Relevé comme amélioration de
robustesse pour un device hôte qui ne draine pas (cas où la file se saturerait
réellement), pas comme correctif du noir/blocage Yotei.

**Plan d'implémentation retenu (à faire plus tard)** :
1. Abaisser le seuil d'attente à ~40-50 ms de latence cible.
2. Ajouter un timeout (~200 ms) après quoi `SDL_FlushAudioStream`/`ClearAudioStream`
   + `break`, copiant `audio.cpp:380-384`, au lieu d'enqueue au-delà du cap.
3. Exposer un garde `SHARPEMU_AUDIO_QUEUE_TARGET_LATENCY_US` pour régler sans
   recompiler. Aucun changement de comportement par défaut hors saturation.

Risque faible (seulement le chemin de contre-pression), mais **à tester sur
un run où le device audio ne draine pas** pour valider — pas sur un run
normal où `blocked` reste à 0.

---

## 5. Question ouverte

**Pourquoi le jeu refuse-t-il d'agrandir 85 arènes sur 89 ?**

### Fait vérifié (désassemblage réel, 2026-08-08)

Outillage : radare2 6.2.0 (portable, `%TEMP%\radare2`, pas dans le PATH —
absent de `winget`, récupéré depuis la release GitHub
`radareorg/radare2` w64 zip). **Piège d'adressage à noter pour toute
prochaine session** : ce binaire (ELF PS5 "self", `pic=false`,
`baddr=0x0` détecté par r2) charge par défaut ses adresses relatives à 0,
alors que SharpEmu mappe le segment à l'exécution sur `0x0000000800000000`
(cf. loader log `Using image base: 0x0000000800000000`). Sans
`-B 0x800000000` au lancement de `r2`/`radare2.exe`, toute adresse au
format `0x800xxxxxxx` pointe hors des sections mappées et désassemble du
`0xff invalid` en boucle — silencieusement faux, aucune erreur. L'ancien
script `scripts/yotei-dissect-return-address.sh` ne passe pas ce flag ; ses
résultats antérieurs sont donc suspects et devraient être rejoués avec la
base corrigée avant d'être réutilisés.

Commande qui fonctionne :
```
radare2.exe -q -e scr.color=false -B 0x800000000 -c "s <addr>; pd 40" eboot.bin
```
(`aa`/`af`/`pdf` échouent sur ce binaire strippé — `ERROR: Cannot find
function at ...` même à la bonne base ; désassemblage linéaire (`pd`) direct
fonctionne, la frontière de fonction a été confirmée manuellement en
vérifiant un `ret`+padding `int3` juste avant `0x8009f5750`.)

Désassemblage confirmé du callback `0x8009F5750` (celui des 85 refus) :

```asm
0x8009f57c8   mov r9d, dword [rdx + 0x900]   ; compteur d'entrées en vol
0x8009f57cf   cmp r9, 0x3f                    ; 63
0x8009f57d3   jbe 0x8009f57d8                 ; <= 63 → continue
0x8009f57d5   xor eax, eax
0x8009f57d7   ret                             ; > 63 → return 0, inconditionnel
0x8009f57d8   mov r10, r9
0x8009f57db   shl r10, 4                      ; slot = r9 * 16
0x8009f57df   mov qword [rdx + r10 + 0x500], rax   ; enregistre l'entrée
0x8009f57e7   mov dword [rdx + r10 + 0x508], ecx
0x8009f57ef   lea ecx, [r9 + 1]
0x8009f57f3   mov dword [rdx + 0x900], ecx          ; incrémente le compteur
```

Table à 64 emplacements (`[rdx+0x500]`…`[rdx+0x8FF]`, 16 octets/entrée),
compteur à `[rdx+0x900]`. **Dans tout le code vu de cette fonction, le
compteur n'est jamais décrémenté** — seulement incrémenté à chaque appel
réussi. Au-delà de 64 appels réussis cumulés, chaque appel suivant retourne
0 inconditionnellement, indépendamment de la disponibilité mémoire réelle.
`rdx` correspond très probablement au 3ᵉ argument du callback
(`user_data`, signature Kyty `(CommandBuffer*, uint32_t, void*)`) — donc une
structure propre au jeu, pas un état SharpEmu.

### RÉFUTÉ (2026-08-08, plus tard la même session) — la théorie du compteur à 0x900

**L'hypothèse ci-dessus est fausse, contredite par des données live.**

Ajout d'un log encadrant l'appel côté C# (`TryAllocateCommandDwords`,
`AgcExports.cs`), lisant `[commandBufferAddress]`, `[commandBufferAddress+0x10]`
et `[userData+0x900]` **avant et après** l'appel au callback guest
(`yotei_20260808_190240_stderr.log`, build `alloc-diag` republié) :

```
pre_field0=0x0 pre_field10=0x0 pre_counter=0x0 post_counter=0x0
```

**Identique sur les 30/30 refus** de `buf=0x804F171A0`, avant et après
l'appel. Si la condition de refus était vraiment `[userData+0x900] > 0x3f`
(désassemblage précédent), un compteur à 0 aurait dû prendre le chemin
succès — il ne le fait jamais. Donc soit la fonction contient une branche de
refus antérieure à la fenêtre de ~70 instructions désassemblée (jamais vue),
soit `[rdi+0x900]`/`[rdi+0x10]`/`[rdi]` ne sont pas les bons champs. **Piste
abandonnée en l'état** — la revoir demanderait un désassemblage complet de la
fonction (pas juste son entrée) ou un vrai pas-à-pas sur l'exécution native,
pas de la relecture statique supplémentaire.

### Fonction complète désassemblée — nouvelle piste, plus solide

`0x8009F5750` est une fonction **entièrement bornée et lue de bout en bout** :
entrée confirmée (ret+padding `int3` juste avant), sortie confirmée à
`0x8009f580d` (`ret`, suivi de padding puis d'une fonction sans rapport qui
commence à `0x8009f5810`). Flux linéaire entre les deux, sans branche
manquée (vérifié instruction par instruction) :

- **Une seule sortie retournant 0** : `0x8009f57d7` (`xor eax,eax; ret`),
  gardée par `[userData+0x900] > 0x3f` — jamais vraie d'après §"RÉFUTÉ"
  ci-dessus (compteur toujours à 0).
- **Une seule sortie retournant 1** : `0x8009f580d` (`mov al,1` posé plus
  haut à `0x8009f57fc`, puis `ret`) — empruntée par **tout le reste du
  flux**, y compris le chemin "skip" (`[rdi]==0`) qui couvre nos 30 cas
  observés.

**Conclusion qui en découle** : si cette fonction s'exécute réellement avec
les valeurs observées, elle ne peut retourner que 1 (succès). Le C# observe
`callbackResult=0` (refus) dans 100 % des cas testés.

**Vérification de `TryCallGuestFunction`** (`DirectExecutionBackend.cs:4345`) :
le mapping d'arguments C#→registres est correct — `arg0`(`commandBufferAddress`)
→ RDI, `arg1`(`sizeDwords+reserved`) → RSI, `arg2`(`userData`) → RDX,
conforme à ce que le désassemblage suppose. Pas de bug de convention d'appel
visible à ce niveau. Le retour se fait via `context[CpuRegister.Rax]`, lu
uniquement si `exitReason` n'est ni `Blocked`(non résolu) ni `Exception` ni
`ForcedExit` — donc l'exécution s'est terminée "normalement" du point de vue
du harnais (pas de trace `agc.cmd_alloc_callback_failed`, qui aurait signalé
un échec d'exécution plutôt qu'un refus).

**Deux explications concurrentes, aucune vérifiée** :
1. Mon traçage du désassemblage a un trou : je n'ai pas suivi jusqu'au bout
   le chemin CAS/fallback de l'allocateur bump (`[0x804f0f608]` compteur,
   `[0x804f0f610]` limite, `[0x804f0f620]` index de repli) faute de connaître
   leurs valeurs live réelles à cet instant précis.
2. `ExecuteGuestThreadEntry` (même fichier) capture `Rax` au mauvais moment,
   ou ne détecte pas correctement la fin d'exécution pour ce type d'appel
   host→guest spécifique (callback invoqué depuis du code C#, pas depuis un
   thread guest normal).

**Prochaine étape** : logger `Rax` juste avant le `ret` réel (ou tracer
`ExecuteGuestThreadEntry`) plutôt que de continuer à relire le désassemblage
statique — la contradiction ne se réglera pas par plus de lecture, il faut
une observation live du registre au bon instant.

### CONFIRMÉ ET CORRIGÉ (2026-08-08, plus tard la même session) — bug RAX générique dans `ExecuteGuestThreadEntry`

Trouvé en lisant le trampoline d'entrée octet par octet
(`DirectExecutionBackend.cs`, autour de la ligne 6045) : le callback invité
est appelé via un `call rax` classique (pas la technique du sentinel écrit
sur la pile). Au `ret` du callback, RAX contient sa vraie valeur de retour,
intacte — le nettoyage qui suit (`add rsp,8`, restauration de RSP via `r10`,
dépilement des registres non-volatils) ne touche jamais RAX. `CallNativeEntry`
capture donc correctement cette valeur dans la variable locale `nativeReturn`.

**Mais `nativeReturn` n'était jamais réinjecté dans `context[CpuRegister.Rax]`**
avant `return GuestNativeCallExitReason.Returned;` — juste utilisé pour un
message de debug puis jeté. Confirmé par grep : sur tout le fichier, les deux
seules écritures de `context[CpuRegister.Rax]` concernent le cas de reprise
après blocage, jamais le retour normal. Ni `TryCallGuestFunction`
(`returnValue = context[CpuRegister.Rax]`) ni `RunGuestThread`
(`thread.ExitValue = thread.Context[CpuRegister.Rax]`, ligne 5777) n'avaient
donc jamais la vraie valeur de retour — seulement le défaut d'initialisation
du `CpuContext` (jamais mis à jour explicitement, donc 0). Bug présent aux
**deux** sites (`ExecuteGuestThreadEntry` et sa jumelle
`ExecuteBlockedGuestThreadContinuation`), corrigé aux deux.

**Fix** : `context[CpuRegister.Rax] = unchecked((ulong)(uint)nativeReturn);`
juste avant chaque `return GuestNativeCallExitReason.Returned;` (extension
par zéro, pas par signe — cohérent avec un callback qui ne manipule que
`eax`/`al`, comme c'est le cas ici).

**Vérifié en live** (`yotei_20260808_191232_stderr.log`, recette propre,
sans béquille, `SHARPEMU_LOG_AGC=1`) :

| Compteur | Avant | Après |
|---|---|---|
| `cmd_alloc_callback_refused` | 85 | **0** |
| `cmd_alloc_callback_complete` | ~4 | **89/89** |

Le désassemblage était donc juste : le jeu répond toujours succès, c'est
SharpEmu qui rapportait un refus. **Bug générique confirmé, indépendant de
Yotei** — candidat sérieux au commit.

**Mais ça n'a rien changé en aval**, ce même run :

| Compteur | Avant | Après |
|---|---|---|
| `agc.dcb.release_mem` (exécutés) | 10 | **10** (identique) |
| `presented guest frame` | 1 | **1** (identique) |
| `driver_submit_dcb_call` / `acb_call` | 1 / 6 | **1 / 6** (identiques) |

**Donc ce bug est réel, corrigé, vérifié — mais ce n'était pas la cause du
blocage de Yotei.** Le refus du callback était un symptôme parallèle, pas ce
qui empêche la suite de s'exécuter. Le vrai verrou (pourquoi seulement 10
`release_mem` sur 83 construits s'exécutent, pourquoi une seule frame) reste
entier et est ailleurs — la piste de la §2 ("88% du travail jamais soumis")
doit être réexaminée à la lumière de ce résultat : agrandir l'arène avec
succès n'a pas suffi à faire exécuter plus de paquets, donc le vrai goulot
n'est probablement pas l'allocation d'arène elle-même.

### Points secondaires non résolus

- **Divergence d'adresse de texture G-buffer** : `0x5000860000` en mémoire
  projet (`yotei-black-framebuffer-rootcause.md`) contre `0x5061C10000` utilisé
  comme cible de `DynamicHiZWatch`. Non réconcilié. Le watch n'a jamais eu un
  seul hit, quelle que soit la configuration.
- **Compositeur `cs=0x8000368D00`** : n'apparaît dans aucun log. Non déterminé
  si l'atteindre dépend du déblocage des arènes ou d'autre chose.

### Shader "plein écran" identifié — `SHARPEMU_SKIP_COMPUTE_CS=0x800036E100`

Trouvé par l'utilisateur (2026-08-08) dans `clean_test/` (logs, pas des
`.md` — voir la correction faite sur la mémoire `yotei-cinematic-display-fix`,
qui affirmait à tort qu'aucune adresse n'était connue). **Vérifié
indépendamment** avant d'être noté ici :

- `grep -ic 800036e100 clean_test/checkpoint4c_8e1e89c0/run1.log` → 26
  occurrences (mon premier essai avait raté le padding hexadécimal complet,
  `0x000000800036E100` vs `0x800036E100` cité — corrigé).
- `groups=240x135x1` confirmé dans les lignes `agc.compute_shader` : bien
  32 400 groupes = 3840/16 × 2160/16, un dispatch plein écran.
- Écart avec le compositeur `0x8000368D00` : `0x800036E100 - 0x8000368D00 =
  0x5400` — exact, recalculé indépendamment.
- Mécanisme vérifié dans le code actuel :
  `src/SharpEmu.Libs/VideoOut/VulkanVideoPresenter.cs:12126-12128`
  (`_skipAllCompute || AddressListContains("SHARPEMU_SKIP_COMPUTE_CS",
  work.ShaderAddress) || (_skipTallComputeZ > 0 && ...)`), liste
  séparée par virgules acceptée.

**Pas encore testé combiné à la recette complète** au moment de cette
entrée — voir le run suivant pour le résultat.

### Le vrai goulot trouvé — fenêtre de suivi d'arène trop étroite (2026-08-08)

Suite logique du fix RAX : si le callback d'agrandissement répond maintenant
honnêtement, pourquoi rien n'a changé en aval (§ précédente) ? Test décisif :
recette propre + `SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES=1` +
`SHARPEMU_LOG_AGC=1` ensemble (`yotei_20260808_193443_stderr.log`), pour
voir si le mécanisme de secours (avec les fixes de ce soir : tracking
`release_mem`, filtre élargi) exécute réellement plus de paquets.

**Résultat, volume** : explosion du débit — `dcb.release_mem` exécutés
10 → **906**, `driver_submit_dcb_call` 1 → **17**, `driver_submit_acb_call`
6 → **105**. Mais `presented guest frame` reste à **2**, identique.
**Conclusion : le débit n'est pas le goulot.** SharpEmu traite très bien des
centaines de paquets — ça élimine toute une classe d'hypothèses "pas assez
de travail exécuté".

**Résultat, ciblé** : les 3 labels bloqués de longue date (§2,
`0x2011831650`/`0x2011669FE0`/`0x2011882330`) ont leurs paquets producteurs
exacts (`cmd=0x20118249F0`/`0x201165CC5C`/`0x20118755C4`) recherchés dans les
906 exécutions réussies. **Zéro occurrence pour les 3**, malgré 236
destinations distinctes satisfaites par ailleurs. Pas rare — structurellement
exclu.

**Pourquoi, trouvé par corrélation d'adresses** : la fenêtre de "tranche
fermée" (`agc.orphan_arena_closed`) que le mécanisme de suivi d'arène
enregistre pour chaque builder est étroite (quelques centaines d'octets,
la zone connue du "paquet de fence répété chaque frame" — voir le grand
commentaire sur `_fenceWritePacketSites` plus haut dans `AgcExports.cs`).
Les 3 paquets cibles sont TOUS en dehors de cette fenêtre, largement plus
loin dans la même arène :

| Label | Fenêtre suivie | Paquet cible | Écart |
|---|---|---|---|
| `0x2011831650` (buf=`0x804F70710`) | `0x201181F400`–`0x8E0` | `0x20118249F0` | ~0x5B10 (23 Ko) |
| `0x2011669FE0` (buf=`0x806AB2030`) | `0x201165C000`–`0x1F8` | `0x201165CC5C` | ~0xA64 (2,6 Ko) |
| `0x2011882330` (buf=`0x804F7AF50`) | `0x2011870300`–`0x504` | `0x20118755C4` | ~0x50C0 (20,7 Ko) |

**Conclusion** : ce n'est pas un problème de décodage de paquet (déjà écarté),
ni de volume/débit (écarté ci-dessus), ni de callback d'arène (corrigé, sans
effet). C'est que le mécanisme de suivi d'arène ne "voit" jamais qu'une
fenêtre étroite près du curseur connu — tout contenu de l'arène situé
significativement plus loin (des Ko à des dizaines de Ko) reste invisible à
TOUS les mécanismes de secours de ce soir, aussi améliorés soient-ils.
**Cible d'ingénierie concrète pour un futur fix** : élargir la fenêtre de
suivi/fermeture de tranche (ou scanner l'arène complète plutôt qu'une
fenêtre proche du curseur) pour ces trois builders au minimum. Non implémenté
ce soir — trouvé et vérifié, pas encore corrigé.

### Vérification visuelle #1 (capture d'écran, 2026-08-08, AVANT les fixes de scan de secours)

Recette : spin-flag + force-submit, ~29s après lancement, capture directe de
la fenêtre. **Écran noir**, seul le HUD de debug de l'émulateur est visible
(`FPS 1.9 PRES 1.9`, `DRAWS 108/S 58/F`, une barre rouge verticale) — aucun
contenu du jeu, pas de logo Sony, pas de cinématique. Confirme visuellement
ce que les logs indiquaient déjà (2 frames présentées puis plus rien), sans
ambiguïté possible sur "peut-être que ça affiche quelque chose que les logs
ne montrent pas".

### Fix : scan de secours étendu aux labels bloqués + data_sel=2 (2026-08-08)

Deux trous précis trouvés en corrélant les adresses des 3 labels
définitivement bloqués contre les tranches suivies par builder :

1. **Fenêtre de suivi trop étroite, mais scan de secours déjà assez large** :
   les 3 paquets cibles sont 2,6-23 Ko au-delà de la fenêtre de "tranche
   fermée" (`agc.orphan_arena_closed`) — mais le scan de secours
   (`SalvageStuckFenceWrites`, fenêtre de 128 Ko par arène) les couvre déjà
   numériquement. Le vrai problème : `destinations` (les cibles recherchées)
   ne contenait que les adresses **déjà apprises** par exécution antérieure
   — jamais les labels **actuellement bloqués** (`GpuWaitRegistry`). Nos 3
   labels n'avaient jamais exécuté une seule fois, donc jamais appris nulle
   part : le scan ne les cherchait tout simplement pas. **Fix** : ajouter
   toute adresse actuellement attendue (`GpuWaitRegistry.SnapshotAll()`) à
   l'ensemble des cibles recherchées.
2. **`data_sel=2` jamais supporté** : `TrySalvageReleaseMemPacket` ne
   validait que `dataSelection==1` (littéral 32 bits) par prudence — mais
   les 3 paquets cibles utilisent tous `data_sel=2` (littéral 64 bits,
   `data=0x0000000000000001`). **Fix** : support ajouté, avec exigence
   `dataHi==0` (strict, n'accepte que le schéma réellement observé).
3. Le scan de secours n'essayait que le validateur `write_data` sur les
   paquets trouvés — jamais `TrySalvageReleaseMemPacket`. **Fix** : essaie
   maintenant les trois formes (write_data, release_mem standard,
   release_mem AGC-nop) sur toute adresse candidate.

**Résultat, vérifié en log** (`yotei_20260808_194212_stderr.log`) : les 3
labels s'exécutent enfin —
`agc.dcb.release_mem dst=... wrote=True` / `agc.queue_resumed` /
`agc.dcb.resumed forced=False` pour les trois, avec des centaines de dwords
de travail débloqués (`remaining_dwords=894/356/758`). Plus aucun label
bloqué en permanence en fin de run (le seul restant, `0x20000002E0`, est un
compteur incrémental qui cycle normalement).

**Effet en cascade, majeur** : le compositeur `cs=0x8000368D00` (0 mention
sur TOUS les runs précédents ce soir) dispatche maintenant **94 fois**, et
— ça règle la divergence d'adresse jamais réconciliée plus haut — écrit
réellement sur **`0x5000860000`** :
```
agc.compute_writer addr=0x0000005000860000 fmt=10 num=0 tile=27 size=3840x2160 cs=0x0000008000368D00 op=ImageStore
```
C'est exactement l'adresse de la note mémoire originale
(`yotei-black-framebuffer-rootcause.md`, "jamais écrite") — donc
**`0x5000860000` était la bonne adresse depuis le début**,
`0x5061C10000` (utilisée par `DynamicHiZWatch` toute la soirée) était
probablement une adresse d'arène d'un run antérieur, différente à chaque
lancement (le piège déjà documenté par le code : "the arena allocator hands
out a different slot every launch").

`driver_submit_dcb_call` 1→26, `driver_submit_acb_call` 6→159 —
soumissions bien plus fréquentes. **Mais `presented guest frame` reste à 2.**

### Vérification visuelle #2 (2026-08-08, APRÈS les fixes de scan de secours)

Lancé visiblement, l'utilisateur regarde son écran directement (pas de
capture prise, sur sa demande). **La cinématique Sony joue** — jamais vu
dans aucun run précédent ce soir. Puis plus d'affichage. HUD :
`DRAWS 16/s`, `PRES 0.2/s` (≈1 frame présentée toutes les 5s, cohérent avec
2 présentations totales sur tout le run). **Le pipeline de rendu tourne
maintenant** (draws réels, pas juste du travail interne) — le verrou restant
est spécifiquement sur le flip/present final, plus sur le rendu lui-même.
**Prochaine piste, non explorée** : le call/export qui déclenche le flip
(`sceVideoOutSubmitFlip` ou équivalent) a probablement son propre verrou,
séparé du compositeur maintenant débloqué.

### 4ᵉ verrou trouvé — un des deux framebuffers n'est jamais dessiné (2026-08-08)

Run visible de 400s+ (`yotei_20260808_194607_stderr.log`, ~1,9M lignes), le
flip cycle réellement maintenant (`vk.flip_capture`/`vk.flip_retired`
récurrents, contrairement à tous les runs précédents). Mais un des deux
buffers du double-buffering échoue systématiquement :

```
vk.flip_capture_failed ... addr=0x0000005007190000 found=False initialized=False   (index=3, échoue toujours)
vk.flip_capture ... addr=0x0000005005160000 ...                                     (index=2, réussit)
```

Les deux buffers sont enregistrés de façon identique
(`agc.display_buffer handle=1 index=2/3 ... fmt=0x8100000022000000 ...`,
3s d'écart), donc légitimement censés alterner. `_guestImages.Add(...)`
(`VulkanVideoPresenter.cs`) n'est **jamais** appelé pour `0x5007190000` —
sur 1,9M lignes, cette adresse n'apparaît que dans sa déclaration initiale
et dans les avertissements d'échec répétés (53 occurrences, aucune autre
mention) : **aucun dessin, aucune écriture compute ne la cible jamais.**

**Écarté** : ce n'est pas un `wait_reg_mem` bloqué comme les 3 précédents —
vérifié, aucun `agc.wait_suspended ... producer=none-observed` dans les
20 000 dernières lignes ; tous les labels actifs cyclent normalement.

**Théorie, non vérifiée** : même cause racine que les 3 labels déjà
corrigés (paquets orphelins par changement d'arène), mais appliquée à des
commandes de **dessin/dispatch**, pas à des écritures de fence. Le
mécanisme de secours réparé ce soir (`SalvageStuckFenceWrites`) ne sait
re-soumettre que des écritures de label — **aucun mécanisme équivalent
n'existe pour ré-exécuter une commande de dessin orpheline.** Différence
importante avec les fixes précédents : rejouer une écriture de fence perdue
est sans danger (idempotent, valeur unique connue à l'avance) ; rejouer une
commande de dessin arbitraire perdue ne l'est pas en général (effets de
bord, ordre, état du pipeline graphique) — un fix ici demanderait plus de
prudence que les trois précédents. Non implémenté ce soir.

**Tentative faite puis annulée** : rafraîchir `_builderArenaLastSeen` à
chaque appel de `TryAllocateCommandDwords` (pas seulement sur `release_mem`
ou l'épuisement d'arène), pour que la tranche fermée couvre aussi les
dessins construits entre deux `release_mem`. **Annulée avant tout test
live** — le commentaire du champ lui-même (`AgcExports.cs`, juste au-dessus
de sa déclaration) dit explicitement que cette généralisation a déjà été
tentée **trois fois** dans des sessions antérieures et a **empiré** le
blocage à chaque fois (12 flips → 6 → 6 → 2), *même en version purement
passive* — raison non totalement identifiée (piste retenue : pression de
verrou/timing sur ce chemin ultra-chaud, qui perturbe l'ordonnancement du
jeu d'une façon qui interagit mal avec le mécanisme orphan-preamble déjà en
place). J'ai reproduit exactement ce pattern déjà rejeté avant de lire ce
commentaire — repéré et annulé immédiatement après. Piste à ne **pas**
retenter sous cette forme ; il faudrait une approche qui ne touche pas ce
chemin chaud du tout (lecture passive périodique hors du chemin d'allocation,
ou diagnostic par point d'arrêt plutôt que modification du parseur).

---

## 6. Recette de run (diagnostic propre)

```powershell
cd ~\Documents\code\sharpemu
$TS  = Get-Date -Format "yyyyMMdd_HHmmss"
$OUT = "debug\yotei_$TS"

$env:SHARPEMU_LOG_AGC = "1"
Remove-Item Env:\SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES -ErrorAction SilentlyContinue
Remove-Item Env:\SHARPEMU_FORCE_SPIN_FLAG_RIP -ErrorAction SilentlyContinue

$p = Start-Process -FilePath ".\artifacts\publish\alloc-diag\SharpEmu.exe" `
  -ArgumentList '"C:\Users\foufouadi\Documents\jeux\ghost\eboot.bin"' `
  -RedirectStandardOutput "${OUT}_stdout.log" `
  -RedirectStandardError  "${OUT}_stderr.log" `
  -PassThru -NoNewWindow
"PID=$($p.Id)  log=${OUT}_stderr.log"
```

Dépouillement (~2 min de run suffisent) :

```powershell
$F = (Get-ChildItem debug\yotei_*_stderr.log | Sort-Object LastWriteTime -Desc)[0].FullName
foreach ($k in 'agc.cb_release_mem','agc.dcb.release_mem','driver_submit_dcb_call',
               'driver_submit_acb_call','agc.cmd_alloc_callback_refused',
               'agc.cmd_alloc_no_callback','agc.cmd_alloc_callback_no_space',
               'agc.wait_suspended','agc.queue_resumed','presented guest frame') {
  "{0,-34} : {1}" -f $k, (Select-String -Path $F -Pattern $k -SimpleMatch).Count
}
```

Attendu si l'analyse tient : ~85 `agc.cmd_alloc_callback_refused` sur 89
callbacks.

---

## 7. Logs de référence (`debug/`)

| Fichier | Configuration | Exploitable |
|---|---|---|
| `yotei_puremain_20260808_181154` | `main` pur, `LOG_AGC` seul | **oui — référence** |
| `yotei_noorphan_20260808_175405` | `main` + patches, sans béquille | **oui** (chiffres identiques au précédent) |
| `yotei_hizwatch_*` (10 runs) | avec `FORCE_SUBMIT_ORPHAN_PREAMBLES` | **non** — sémantique d'attente altérée |

---

## 7bis. ERREUR MÉTHODOLOGIQUE CORRIGÉE — "2 frames présentées" était un faux signal

Toute la soirée, `grep -c "Vulkan VideoOut presented"` a servi de métrique
principale pour "le jeu est figé". **C'était le mauvais indicateur.**

`VulkanVideoPresenter.cs:16798-16810` : les deux lignes
(`presented first frame` / `presented guest frame`) sont gardées par des
booléens one-shot (`_firstFramePresented`, `_firstGuestDrawPresented`) —
elles ne s'affichent **qu'une seule fois sur toute l'exécution**, quel que
soit le nombre réel de frames présentées ensuite. Compter ces lignes ne
mesure pas un débit de présentation, juste "le jalon A et le jalon B ont
été atteints une fois chacun".

**Métrique correcte** : `vk.flip_capture version=N` (une ligne par capture
réussie, pas de garde one-shot). Run de ~5 min avec le dernier fix
(`yotei_20260808_195906_stderr.log`) : 51 succès ≈ 0,17 frame/s — cohérent
avec le `PRES 0.2/s` observé en direct sur le HUD de l'utilisateur. Le jeu
présente donc probablement en continu depuis le début de la soirée, très
lentement (buffer index=2 seul, index=3 toujours cassé — §"4e verrou"), pas
figé sur une frame unique comme conclu à tort plus haut dans ce document.

**Conséquence** : toute affirmation antérieure dans ce document utilisant
"frames présentées = 2" comme preuve de blocage total est à relire avec
cette correction en tête — le vrai symptôme est un débit de présentation
anormalement bas (~0,2 fps), pas un gel absolu après 2 frames. Repéré en
lisant le code source de `VulkanVideoPresenter.cs` après que le comptage de
lignes de log a cessé de coller à l'observation visuelle directe de
l'utilisateur (HUD montrant `PRES 0.2/s`, une valeur non nulle) — exactement
le genre d'erreur que la règle du §8 ci-dessous existe pour éviter.

**Contre-vérification faite** : hypothèse "le débit de 0,2 fps vient du
surcoût de `FORCE_SUBMIT_ORPHAN_PREAMBLES` lui-même, pas d'un vrai blocage"
— **réfutée**. Recette propre (sans la béquille, mêmes fixes,
`yotei_20260808_200633_stderr.log`, 2 min) : retombe exactement sur les
chiffres d'origine du tout début de cette investigation
(`cb_release_mem=83`, `dcb.release_mem=10`, compositeur à **0** dispatch,
**1 seul** `flip_capture` réussi). `FORCE_SUBMIT_ORPHAN_PREAMBLES` n'ajoute
pas de bruit qui ralentirait un jeu par ailleurs rapide — c'est lui qui
débloque le compositeur et multiplie les flips réussis par 51. Nécessaire,
pas nuisible.

**Bilan honnête de ce soir, avec la métrique corrigée** : le jeu passe de
"aucun flip après les 2 premiers jalons" (compositeur à 0, 1 flip) à
"présentation continue à ~0,2 fps, compositeur actif, buffer index=2
fonctionnel" grâce aux fixes de fence-salvage (release_mem, data_sel=2,
scan de secours élargi). Le buffer index=3 reste cassé (§"4e verrou") et le
débit global (~0,2 fps) reste très en dessous d'un framerate jouable — mais
ce n'est plus un gel total, c'est une présentation continue anormalement
lente. Prochaine question ouverte : pourquoi ~0,2 fps et pas plus, sachant
que `FORCE_SUBMIT_ORPHAN_PREAMBLES` est confirmé nécessaire (pas la cause du
ralentissement) — le tempo semble suivre `DRAWS/s` du HUD, donc peut-être
un vrai problème de performance de dessin, distinct de tout ce qui a été
corrigé ce soir.

**Piste testée** : le coût de `SHARPEMU_LOG_AGC=1` lui-même. Une fenêtre
entre deux flips (`yotei_20260808_195906_stderr.log`) contient **12 790**
lignes `agc.dcb.packet`/`agc.dcb.payload` (trace paquet-par-paquet, gardée
par `_traceAgc`) contre seulement 56 lignes de dessin et 1 dispatch compute
— l'essentiel du volume de log est de la verbosité diagnostique, pas du
travail réel. Test : même recette, `LOG_AGC` retiré (observation directe du
HUD par l'utilisateur, `vk.flip_capture` étant lui-même gardé par
`_traceAgc` donc invisible sans le flag). **Résultat : `PRES` 0,2 → 0,4** —
2x plus rapide, donc le coût de la trace est réel, mais **0,4 fps reste très
loin d'un framerate jouable** (30-60 attendu). Le logging n'est qu'une
fraction du problème.

**Piste ouverte, non testée ce soir** : `CPU 24%` observé sur le HUD (run
antérieur) suggère que l'émulateur n'est *pas* saturé en CPU — cohérent avec
un goulot **sérialisé mono-thread**, pas un manque de puissance de calcul.
Le mécanisme `orphan_preamble` (confirmé nécessaire, pas nuisible — voir
ci-dessus) fait un volume de travail important sous un seul verrou
(`_orphanPreambleGate`, `SweepBuilderArenas`/`SalvageStuckFenceWrites`
tournant en série) : 285 `orphan_preamble_force_submit` rien que dans UNE
fenêtre inter-flip. Hypothèse à vérifier : ce travail sérialisé, pas le
rendu du jeu lui-même, borne le débit. Nécessiterait du profilage
(mesure de temps par section, pas de la lecture de logs) pour confirmer —
nature de travail différente de tout ce qui a été fait ce soir.

**Outil de profilage déjà existant, utilisé** : `SHARPEMU_TIME_WAIT_MONITOR=1`
(`AgcExports.cs:197`), construit par une session antérieure pour exactement
cette question — son propre commentaire dit *"the loop body blocks ~5s/
iteration"* (`HIZ_INVESTIGATION.md` run #3/#4, fichier disparu). Un budget
(`OrphanDrainBudgetMs=20ms`) existe déjà pour borner CHAQUE itération.

**Résultat, ce soir** : aucune itération individuelle ne dépasse le seuil de
50ms du diagnostic (rien loggé, `Stall gpu_wait_monitor` absent). Le budget
fonctionne comme prévu — mais avec 285 `orphan_preamble_force_submit` par
frame observés plus haut, il faut des dizaines d'itérations bornées pour
écouler le travail d'une seule frame : le budget répartit le coût sur plus
d'itérations, il ne réduit pas le total cumulé (~3-6s/frame malgré tout).

**Vraie question, non résolue ce soir** : pourquoi la soumission normale
par curseur ne couvre-t-elle presque rien, forçant la quasi-totalité du
travail de chaque frame à passer par le chemin de secours lent et
sérialisé (`_orphanPreambleGate`) ? C'est une question d'architecture plus
large que les bugs ponctuels corrigés ce soir (RAX, salvage `release_mem`,
data_sel=2) — probablement le vrai sujet de la prochaine session.

**Évaluation ci-dessus RÉFUTÉE par l'utilisateur, à raison** : "si ça tourne
à 3 fps je devrais voir une image qui change plus lentement, pas rien."
Argument correct — un débit bas explique un slideshow perçu comme lent, pas
un noir permanent. L'hypothèse "juste de la latence" ne tenait pas.

### Vraie cause du noir — le G-buffer d'entrée n'est jamais rempli (2026-08-08)

En creusant la remarque ci-dessus : le compositeur `cs=0x8000368D00` LIT bien
depuis `0x5061C10000` (bindings de son dispatch,
`ImageLoad@0x8C:0x0000005061C10000:...texels=00000000/00000000/00000000`)
— **exactement l'adresse identifiée dans le tout premier message de cette
investigation** ("cette texture est toujours à zéro, jamais écrite"),
maintenant confirmée avec certitude au lieu d'une hypothèse.

**Vérifié, pas seulement ce dispatch** : sur l'ensemble du log
(`yotei_20260808_195906_stderr.log`), `0x5061C10000` apparaît dans les
bindings de **3 shaders compute distincts** (`0x8000368D00`,
`0x8000376E00`, `0x8000408700`), toujours en `ImageLoad` (lecture), **jamais
en `ImageStore`** (écriture), et aucun `agc.rt_writer` (écriture par un
draw) ne la cible non plus. Rien, nulle part dans le log, n'écrit cette
texture.

**Plus large que prévu** : ce n'est pas seulement cette texture. Le dispatch
complet du compositeur (`bindings=[...]`, ~24 entrées) montre que
**pratiquement toutes** les textures qu'il touche — entrées ET sorties —
lisent `texels=00000000/00000000/00000000` (ou l'équivalent en plus petit
format). Seule exception : une texture 1×1 constante
(`0x50091C0400`, `texels=000000FF`, probablement une couleur par défaut).
Le compositeur calcule correctement sur des entrées vides et produit une
sortie vide — il n'est pas cassé, il est **affamé**. Le vrai trou est en
amont : la passe de rendu de la scène 3D (géométrie, matériaux) qui
devrait remplir ces G-buffers ne le fait jamais, ou jamais avant que le
compositeur ne s'exécute.

**Conséquence sur le diagnostic "performance" ci-dessus** : partiellement
valide (le débit de présentation est réellement bas et `FORCE_SUBMIT` est
réellement nécessaire), mais **ce n'est pas la cause du noir**. Même à un
débit élevé, tant que le G-buffer reste vide, l'image présentée serait
noire à chaque frame. Les deux problèmes sont réels et distincts ; celui-ci
est le plus fondamental et le plus proche de la cause originelle du bug.

**Prochaine étape, pour une session future** : identifier quel(s)
dispatch/draw devrai(en)t écrire `0x5061C10000` (et les textures voisines
dans le même schéma) — chercher dans le désassemblage ou les traces
`agc.compute_shader`/`agc.rt_writer` s'il existe un pass de géométrie/
G-buffer-fill qui échoue silencieusement, ne se déclenche jamais, ou écrit
à une adresse légèrement différente de celle attendue. Piste distincte de
tout ce qui a été corrigé ce soir (qui portait sur la plomberie de
soumission/synchronisation GPU, pas sur le contenu du rendu lui-même).

---

## 7ter. Bug réel trouvé et corrigé — retry des dispatches indirects jamais câblé (2026-08-08, suite)

En suivant la piste "pourquoi si peu de `agc.rt_writer`/`agc.compute_writer`
distincts", trouvé `agc.dispatch_reject reason=zero-dimension` dans les logs
(`AgcExports.cs`, `TryReadComputeDispatch`) : un dispatch indirect dont le
buffer de dimensions lit `0/1/1` à l'instant du parse — le dispatch qui
devrait avoir écrit ces dimensions (compteur de visibilité/culling
GPU-driven) ne s'est pas encore exécuté.

**Le code contenait déjà, complet et correct, tout le mécanisme pour gérer
ça proprement** : `HandleSubmittedIndirectDimsWait` (même fichier,
~ligne 9018) enregistre un `GpuWaitRegistry.WaitingDcb` sur l'adresse des
dimensions et suspend le DCB jusqu'à ce qu'elles deviennent non nulles
(budget 150 ms avant abandon définitif) — exactement le même schéma que
`HandleSubmittedWaitRegMem`/`HandleSubmittedRewind`, avec le commentaire
d'intention explicite ("rather than dropping the work, which black-screens
GPU-driven games like Astro Bot"). **Mais elle n'était jamais appelée.**
`TryReadComputeDispatch` calcule bien `indirectDimsRetryAddress` en sortie,
mais le seul site d'appel (`ParseSubmittedDcbCore`, ~ligne 7167) le jetait
avec `out _` et se contentait de laisser tomber le paquet.

**Correctif appliqué** (`AgcExports.cs`, ~ligne 7167) : capture de
`indirectDimsRetryAddress`, et sur échec de `TryReadComputeDispatch` avec une
adresse de retry non nulle, appel de `HandleSubmittedIndirectDimsWait` ; si
elle retourne `true` (enregistrée), `return true` pour suspendre le DCB,
identique au pattern des trois autres suspensions dans la même fonction.
Compile sans erreur/warning nouveau. Actif par défaut (`_gpuWaitSuspendEnabled`
est vrai sauf `SHARPEMU_GPU_WAIT_MODE=force`), aucune nouvelle variable
d'environnement nécessaire.

**Testé** (`debug/yotei_dispatchretry_20260808_223334_stderr.log`, build
`artifacts/publish/dispatch-retry`, recette habituelle
`SHARPEMU_FORCE_SPIN_FLAG_RIP` + `SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES`) :
aucun crash, `vk.flip_capture_failed` (log inconditionnel) passé de non-nul
lors des sessions précédentes à **0 occurrence** sur tout le run, et
l'utilisateur rapporte en direct **34 draws/s (contre 16/s avant), 0.9
présentations/s** — nette amélioration du débit réel de travail GPU.
**Mais l'utilisateur confirme qu'il n'y a toujours aucun affichage réel**
("0.9 press 34 draw/s 0 fps mais pas d'affichage").

**Conclusion** : correctif réel et gardé (résout un vrai bug de plomberie —
des dispatches indirects légitimes étaient abandonnés au lieu d'être
réessayés), mais **insuffisant seul** pour l'affichage. Cohérent avec la
§7bis : le compositeur tourne, présente, mais sur un G-buffer toujours vide.
Soit les dispatches débloqués par ce fix ne sont pas ceux qui remplissent
`0x5061C10000` (et les textures voisines), soit ils s'exécutent mais
écrivent ailleurs, soit le vrai pass de remplissage n'est même pas de type
`ItDispatchIndirect` (peut-être direct, ou un draw classique). Nouveau run
lancé avec `SHARPEMU_LOG_AGC=1` (`debug/yotei_logagc_20260808_223958_*.log`)
pour capturer `agc.rt_writer`/`agc.compute_writer` complets et identifier
enfin le producteur attendu — voir la suite de ce document pour le résultat.

## 7quater. Nouveau blocage trouvé — livelock des threads audio ScreamWorker1/2 (2026-08-08, run LOG_AGC)

Résultat du run `SHARPEMU_LOG_AGC=1` lancé en 7ter
(`debug/yotei_logagc_20260808_223958_stderr.log`, ~1,16M lignes avant arrêt
manuel) :

**Fait mesuré** : sur les **700 000 dernières lignes** du log (`tail -n
700000 | grep -c "agc\.\|vk\."` → **0**), il n'y a **strictement aucune**
activité AGC/GPU — ni dispatch, ni draw, ni writer, ni salvage, ni packet.
100 % des lignes de cette fenêtre sont soit `cooperative_block_*` soit
`guest_threads.claimed_for_execution`
(`DirectExecutionBackend.cs`), réparties ainsi
(`grep -oE "name='[A-Za-z0-9_]+'" | sort | uniq -c`) :

```
280827 ScreamWorker1
280120 ScreamWorker2
 16676 snd_stream_parsing_thread
    16 snd_stream_reader_thread
```

**Comptages de construction/exécution de draws restés strictement figés**
entre deux relevés espacés de 665 000 lignes de log (à 314k puis à 979k
lignes) : `agc.dcb_draw_index_auto` = 21 dans les deux cas,
`agc.compute_writer` = 145 dans les deux cas, `agc.rt_writer` = 4 dans les
deux cas, 39 shaders compute distincts dans les deux cas. Le jeu a donc
cessé toute activité de rendu/soumission GPU à un instant donné, et tout ce
qui a suivi (des centaines de milliers de lignes, du temps CPU réel
consommé — pas un freeze silencieux) est uniquement les deux threads audio
"Scream" (middleware audio interne Sony) qui se réveillent et se rendorment
en boucle l'un l'autre via `cooperative_block_resumed`/
`cooperative_block_wake_no_match`.

**Hypothèse, non encore vérifiée** : ping-pong mutex/cond entre les deux
threads audio, censé être cadencé par la disponibilité réelle d'un tampon
audio matériel (qui bloquerait quelques millisecondes sur du vrai
hardware), mais qui ici ne bloque jamais réellement — donc les deux threads
se repassent le relais à pleine vitesse CPU au lieu d'être rythmés. Si le
modèle d'exécution de threads invités a une capacité limitée de threads
hôtes concurrents, ce livelock pourrait affamer le thread de rendu/soumission
au lieu de seulement gaspiller du CPU en parallèle — à vérifier :
`DirectExecutionBackend.cs`, la file `_readyGuestThreads` /
`ExecutorActive` (~ligne 7181) fait tourner du round-robin coopératif, pas
un vrai pool illimité de threads OS ; reste à confirmer si le
thread de soumission GPU est concrètement bloqué en attente derrière ce
tourbillon ou s'il tourne en parallèle sans rapport.

**Statut** : nouveau blocage distinct de tout ce qui a été corrigé/investigé
ce soir (le fix retry-dispatch en 7ter est réel et gardé, mais ce livelock
audio semble être ce qui coupe court à toute progression ultérieure, avant
même d'atteindre la question du G-buffer de la §7bis). **Reproduit deux fois
de suite** (runs `yotei_dcbwatch_20260808_224743` et
`yotei_periodic_20260808_224958`) — pas un one-off.

## 7quinquies. Approfondissement — FaWorkerIo1 figé, pas seulement l'audio (2026-08-08)

Deux nouveaux runs avec les diagnostics déjà existants dans le code (pas de
nouvelle instrumentation) :

- `SHARPEMU_WATCH_DCB_SUBMIT=1` (`yotei_dcbwatch_20260808_224743_stderr.log`)
  — déclenche un snapshot complet dès que `sceAgcDriverSubmitDcb` est
  silencieux >3s. Premier déclenchement à t=4,2s (trop tôt, encore le
  chargement/la cinématique, 1 seule soumission totale) mais donne un
  premier échantillon exploitable.
- `SHARPEMU_PERIODIC_SNAPSHOT_SECONDS=5` (`yotei_periodic_20260808_224958_stderr.log`)
  — dump complet toutes les 5s, indépendamment de toute détection de stall.
  **6 snapshots consécutifs (30s+), résultat identique à chaque fois** :

```
Stall main-thread-os: tid=13100 alive=yes running live_rip=0x0000000800CD5002 live_rsp=0x00007FFFF01FE370
Stall guest-thread: handle=... name='FaWorkerIo1' state=Blocked imports=608 nid=WKAXJ4XBPQ4
  ret=0x0000000800BAFD9B rdi=0x0000000804C4BC40 rsi=0x0000000804C4BC38 rdx=0x0000001000F0AD01
  block=pthread_cond_wait
```

**`imports=608` ne bouge pas d'un seul appel en 30+ secondes**, alors que
dans le même intervalle des logs `cooperative_block_resumed
thread=...FaWorkerIo1... wake_key=pthread_cond_waiter:N` défilent avec un
`N` qui progresse (1,3,4,5,6,7,8,9,10,11,12,14,...) et un
`guest_threads.claimed_for_execution` à chaque fois — le thread est bien
réveillé et repris par le dispatcher coopératif encore et encore, mais son
compteur d'imports (qui ne s'incrémente qu'à un vrai retour en code invité)
ne progresse jamais : il retombe systématiquement dans le **même** appel
`pthread_cond_wait` (même nid, même `ret`, mêmes `rdi/rsi/rdx`) sans jamais
réellement rendre la main au jeu entre deux réveils.

**Désassemblage de `live_rip=0x0000000800CD5002`**
(`radare2.exe -B 0x800000000`, méthode déjà validée en §5) : c'est
au milieu d'une boucle d'attente active classique à budget RDTSC —
lit un flag à `[0x806e76d80]`, si non-nul boucle sur deux `rdtsc`
consécutifs en comparant contre une deadline calculée (`cmp rdx,rcx; jl`
retour en boucle), et si le flag est nul appelle un import
(`sym.imp.3GPpjQdAMTwzv`) puis, selon son retour, soit reboucle dans le
spin soit calcule un nouveau budget adaptatif et appelle un second import
(`sym.imp.9rAeANT2tyEzv`) — un spinlock adaptatif standard (spin borné puis
fallback sur une primitive de blocage réelle). **Mais un vrai spinlock actif
ne devrait JAMAIS retomber sur le même octet 6 fois de suite à 5s
d'intervalle** (des milliards d'itérations s'écouleraient entre deux
relevés) — donc soit ce thread OS précis n'est en réalité pas exécuté du
tout par Windows pendant cette fenêtre (juste son dernier RIP connu reste
figé, cohérent avec le modèle d'exécuteurs coopératifs à pool partagé —
`_readyGuestThreads`/`ExecutorActive`, §7quater), soit le `hostThreadId`
mis en cache pour ce thread invité est devenu obsolète (le pool réattribue
des threads OS différents à chaque reprise). **Distinction non tranchée** —
la lecture `live_rip` ne doit pas être sur-interprétée seule ; le signal
solide est le compteur `imports` figé, pas ce RIP.

**Deux hypothèses concurrentes pour le mécanisme exact, aucune encore
vérifiée** :
1. **Famine sur `_guestThreadGate`** (`DirectExecutionBackend.cs:698`, un
   simple `lock(object)` .NET, non garanti FIFO) — pris à plus de 20 sites
   d'appel, y compris à chaque dispatch d'import. Si `ScreamWorker1`/`2`
   l'acquièrent des dizaines de milliers de fois par seconde, un `lock`
   .NET standard peut affamer un 3ᵉ thread nettement moins fréquent pendant
   un temps arbitrairement long sous contention soutenue.
2. **Réveil parasite mal ciblé** : `wake_key` de `FaWorkerIo1`
   (`pthread_cond_waiter:N`) et ceux de `ScreamWorker1/2`
   (`pthread_mutex_waiter:N`) partagent visiblement un compteur global
   unique — si un réveil censé cibler un seul waiter en touche
   accidentellement d'autres (clé trop large/aliasing), `FaWorkerIo1`
   pourrait être réveillé en boucle pour rien, re-vérifier son prédicat côté
   invité (`while(!predicate) pthread_cond_wait(...)`), le trouver toujours
   faux, et se rendormir aussitôt — cohérent avec `imports` figé si le
   dispatcher se re-bloque lui-même avant même de rendre la main au code
   invité (donc sans dispatch d'import réellement compté).

**Ne PAS toucher `_guestThreadGate` sans un test A/B prudent** : c'est un
verrou chaud sur plus de 20 sites, remplacer son type de verrou à l'aveugle
risque une régression de perf ou de correction sur tout le pipeline
d'imports, pas seulement ce cas.

**Prochaine étape concrète** : tracer, côté C#, le chemin exact emprunté
quand `FaWorkerIo1` est repris après `cooperative_block_resumed` — savoir
s'il exécute ne serait-ce qu'une seule instruction invitée avant de se
reparquer, ou s'il se reparque depuis le code hôte (C#) sans jamais
redonner la main au JIT. Ça tranche entre les deux hypothèses ci-dessus
sans toucher au verrou lui-même.

## 7sexies. Hypothèse 2 ci-dessus réfutée ; piste `scePthreadCondSignal`↔mutex trouvée dans le code source (2026-08-08)

**Correction** : l'hypothèse "réveil parasite par aliasing de wake_key" (§7quinquies,
point 2) est **fausse** — vérifié en lisant `KernelPthreadCompatExports.cs:1596-1605` :
`waiter.WakeKey` est généré par `Interlocked.Increment(ref
_nextSynchronizationWaiterId)`, un compteur global **strictement unique par
attente enregistrée**, pas partagé/aliasé entre `pthread_cond_waiter:N` et
`pthread_mutex_waiter:N`. Les deux préfixes puisent juste dans le même
compteur global (d'où les nombres entrelacés observés), ils ne se
collisionnent pas.

**Nouveau run** (`SHARPEMU_LOG_GUEST_THREADS=1`,
`yotei_gthreads_20260808_225632_stderr.log`, 296k lignes) : `FaWorkerIo1`
n'est repris (`Pumping guest thread`) que **33 fois sur tout le run**,
et le `resume=` alterne entre exactement deux adresses, jamais une
troisième : `0x0000000800BAFD18` (5×) et `0x0000000800BAFD9B` (27×) — le
thread ne progresse structurellement nulle part ailleurs.

**Désassemblage de la fonction englobante** (`0x800BAFCD0`–`0x800BAFDBF`,
radare2 `-B 0x800000000`) : c'est le wrapper condvar/mutex complet.
Structure lue instruction par instruction :
- `0x800bafd18`: incrémente un compteur puis entre une **boucle spin native
  pure** (`0x800bafd20`–`0x800bafd3e`) qui appelle deux fonctions internes au
  jeu (`0x800ba9fb0`, `0x800baa1f0` — pas de préfixe `sym.imp.`, donc pas des
  imports HLE, aucun appel à SharpEmu) tant qu'un prédicat sur `[rbx+4]`
  n'est pas satisfait — **structurellement identique** au pattern déjà connu
  du spin-flag `0x800D92942` (même style : flag à `[rbxOFFSET]`, boucle
  native pure, pas de coût d'import).
- Après la boucle : rlock/unlock via de vrais imports HLE
  (`sym.imp.cmo1RIYva9oyJ`, `sym.imp.2Tb92quprl0yJ`, `sym.imp.9UK1vLZQft4yJ`,
  `sym.imp.tn3VlD0hG60yJ`), puis `call sym.imp.WKAXJ4XBPQ4yJ` à
  `0x800bafd96` (= `scePthreadCondWait`, confirmé — `ret=0x800bafd9b` colle
  exactement à l'adresse de retour vue dans les logs).
- Après le retour de `cond_wait` : `cmp byte [rbp-0x38],1; jne
  0x800bafca0` — **rebouclage sur le début de toute la fonction** si un flag
  local n'est pas à 1, donc un vrai retry complet (ré-acquisition mutex,
  ré-attente) plutôt qu'une simple relecture de flag.

**Code source de `scePthreadCondWait`** (`KernelPthreadCompatExports.cs:1546`,
`PthreadCondWaitCore`) lu en entier : enregistre un `PthreadCondWaiter` dans
la file d'attente du condvar, déverrouille le mutex associé, puis appelle
`GuestThreadExecution.RequestCurrentThreadBlock(ctx, ..., waiter.WakeKey,
completionCallback: CompleteBlockedCondWait, resumeCheckCallback:
TryGrantCondWaiterMutex)`. **Point clé** (`TryGrantCondWaiterMutex`,
ligne 1996) :
```csharp
if (waiter.CompletionState == 0 || mutexWaiter is null) return false;
```
Le mutex ne peut être accordé au waiter **que si `CompletionState` a déjà
été mis à autre chose que 0** — ce qui n'arrive que si le condvar a
effectivement été signalé (`scePthreadCondSignal`/`Broadcast`,
`PthreadCondSignalCore`, NID `kDh-NfxgMtE` — **exactement le NID que
`ScreamWorker1` appelait 80 113 fois** dans le snapshot du run précédent,
§7quater). **Hypothèse resserrée, pas encore prouvée** : soit
`FaWorkerIo1` attend sur un condvar que personne ne signale jamais
réellement (signal destiné à un AUTRE thread/objet, ou jamais émis par le
bon sous-système), soit il EST bien signalé (peut-être par un signal
`Scream`-adjacent sans rapport logique mais qui touche la même condvar par
erreur d'implémentation HLE) mais la logique de retry côté guest
(`jne 0x800bafca0`) le renvoie systématiquement en attente pour une autre
raison (mutex non regagné, `TryGrantMutexWaiterLocked` échouant en boucle
si le mutex est constamment recapturé par quelqu'un d'autre entre-temps).

**Ce paragraphe et le §7quinquies proviennent de DEUX runs différents** —
le compteur `imports` figé à 608 (§7quinquies) et les 33 reprises à 2
adresses (ce paragraphe) n'ont **jamais été mesurés dans le même run**. Les
deux sont probablement la même chose vue sous deux angles, mais ce n'est
pas prouvé — à confirmer avec un seul run combinant
`SHARPEMU_LOG_GUEST_THREADS=1` et un compteur d'imports affiché en direct
avant d'aller plus loin.

**Prochaine étape la plus directe** : tracer `TryGrantCondWaiterMutex` et
`PthreadCondSignalCore` (ajouter un `Console.Error.WriteLine` conditionnel,
faible risque, aucune modification de comportement) pour voir, dans UN
run, si `FaWorkerIo1`'s waiter reçoit jamais un `CompletionState != 0`, et
si oui, pourquoi `TryGrantMutexWaiterLocked` échoue quand même ensuite.
C'est la question qui reste ouverte pour clore ce blocage.

## 7septies. Cause probable trouvée — `ScreamWorker1/2` réellement bloqués dans `winmm.dll`, pas un livelock (2026-08-08)

**Nouveau diagnostic ajouté au code** (faible risque, gate existant réutilisé) :
`DirectExecutionBackend.cs`, site de déférence d'exécuteur
(~ligne 7184, où un thread `Ready` mais encore `ExecutorActive` est
re-mis en file au lieu d'être lancé) — quand `ExecutorClaimDeferrals >= 32`
(puissances de 2 seulement, donc peu bavard), capture maintenant le
contexte OS **live** du thread hôte propriétaire via
`TryCaptureHostThreadContext` (le même mécanisme que `Stall main-thread-os`,
mais appliqué à N'IMPORTE QUEL thread invité, pas seulement le thread
d'entrée) et logue `owner_rip`/`owner_rsp` sous
`guest_threads.defer_owner_stuck`. Compile propre, republié dans
`artifacts/publish/dispatch-retry`.

**Résultat, run `yotei_execstuck_20260808_230240_stderr.log`** :
```
guest_threads.defer_owner_stuck name='ScreamWorker1' owner_host_tid=18000 owner_rip=0x00007FFBE8A00484 deferrals=64
...
guest_threads.defer_owner_stuck name='ScreamWorker1' owner_host_tid=18000 owner_rip=0x00007FFBE8A00484 deferrals=8192
guest_threads.defer_owner_stuck name='ScreamWorker2' owner_host_tid=15440 owner_rip=0x00007FFBE8A00ED4 deferrals=8192
```
**`owner_rip` est un ADRESSE HÔTE (`0x7FFB...`), pas invitée (`0x800...`)** —
`ScreamWorker1`/`2` ne sont pas coincés dans du code de jeu émulé du tout :
leur thread OS réel est englouti dans une DLL native Windows, identique sur
`64→128→256→512→1024→2048→4096→8192` déférences consécutives (donc des
secondes, peut-être davantage, sans revenir).

**Localisé au code source** : `src/SharpEmu.HLE/Host/Windows/WindowsWaveOutAudio.cs:69`
— `if (!_completion.WaitOne(TimeSpan.FromSeconds(1))) { ... }`, un
`AutoResetEvent.WaitOne` avec **timeout de 1 seconde**, signalé par le
callback `waveOutWrite`/`winmm.dll` quand un buffer audio termine sa
lecture. **Ce n'est PAS un livelock ni un deadlock au sens strict** — c'est
une attente bornée légitime (elle revient forcément après 1s même sans
signal). Mais avec un budget de 1s par tour et `owner_rip` échantillonné
comme figé pendant des milliers de passes du dispatcher, **la quasi-
totalité du temps de ce thread est structurellement passée à l'intérieur
de cet appel** — cohérent avec un design de polling normal, MAIS :

**La vraie question qui reste ouverte** : le nombre d'événements
`cooperative_block_resumed`/réveils observés pour `ScreamWorker1/2` cette
nuit (280 000+ sur une portion de run, §7quater) est bien trop élevé pour
un cycle de 1 seconde — un vrai flux audio ne devrait réveiller ce thread
que quelques dizaines de fois par seconde au grand maximum. **Hypothèse la
plus probable maintenant** : quelque chose (peut-être le pompage du
dispatcher lui-même, `Thread.Sleep(1)` + `Pump()` en boucle serrée) réveille/
ré-enfile `ScreamWorker1/2` dans `_readyGuestThreads` bien plus souvent que
nécessaire alors qu'ils sont encore légitimement occupés dans ce `WaitOne`
d'une seconde — noyant la file d'attente du dispatcher sous des dizaines de
milliers d'entrées à re-différer par seconde, et reléguant les threads
réellement prêts (dont `FaWorkerIo1`) très loin derrière dans chaque passage
— pas un blocage permanent, mais une famine de débit sévère qui, à l'échelle
d'observation de cette nuit (quelques dizaines de secondes à quelques
minutes par run), est indiscernable d'un freeze complet.

**Prochaine étape concrète et bornée** : trouver QUI ré-enfile
`ScreamWorker1`/`2` dans `_readyGuestThreads` si souvent alors qu'ils sont
`ExecutorActive`. Chercher les appelants de `WakeBlockedThreads`/
`TryConsumeWakeLatch` ciblant leur wake-key, et vérifier s'il existe un
signal audio (buffer-ready, tick d'horloge audio,
`GuestAudioClock.cs`?) émis à une fréquence bien supérieure à celle du vrai
matériel simulé.

**Lu en entier, `WaveOutStream.Submit`** (`WindowsWaveOutAudio.cs:56-79`) —
mécanisme de contre-pression : tient `lock (_gate)` pendant tout l'appel ;
si la file interne dépasse `_maximumQueuedPcmBytes` (32 Kio par défaut,
≈170 ms de PCM stéréo 16-bit/48 kHz), bloque sur
`_completion.WaitOne(1s)` en attendant qu'un callback `winmm` signale
qu'un buffer a fini de jouer (`ReapCompletedBuffers`), et **abandonne
silencieusement** (`return false`, buffer jamais soumis) si rien ne se
passe en 1 seconde. C'est exactement l'adresse `owner_rip` capturée en
7septies. Si le callback de complétion `winmm` ne se déclenche jamais ou
trop rarement dans cet environnement (pas de vrai périphérique audio actif,
ou callback cassé), CHAQUE `Submit` qui tombe sur une file pleine coûterait
~1s complète — mais ça ne colle pas seul avec les 80 000+ imports observés
en quelques minutes (aurait pris des heures). Donc soit la plupart des
`Submit` NE bloquent PAS (file rarement pleine, cohérent avec un design
sain), et seule une poignée d'appels malchanceux tombe sur le blocage
`WaitOne` — mais alors pourquoi `owner_rip` y reste-t-il figé sur des
MILLIERS de passes consécutives du dispatcher (§7septies) ? Les deux faits
ne sont pas encore réconciliés.

## 7octies. Hypothèse WaveOut RÉFUTÉE ; adresse figée résolue à `ntdll.dll` générique (2026-08-08, fin de session)

**Compteurs ajoutés** (`WindowsWaveOutAudio.cs`, opt-in
`SHARPEMU_TRACE_WAVEOUT=1`, aucun changement de comportement) :
`_submitCalls`/`_submitBlocked`/`_submitTimedOut`/`_buffersReaped`, tracés
via `waveout.submit#N`/`waveout.submit_timeout#N`. Compile propre,
republié.

**Test** (`yotei_waveout_20260808_230746_stderr.log`) : `guest_threads.defer_owner_stuck`
s'est déclenché normalement pour `ScreamWorker1`/`2` (mêmes adresses
qu'avant), **mais aucune ligne `waveout.submit#` n'est jamais apparue** —
`WaveOutStream.Submit` n'a été appelée strictement aucune fois pendant
toute la fenêtre observée. **L'hypothèse WaveOut (§7septies) est donc
réfutée** : le thread bloqué n'est pas dans ce code.

**Résolution de l'adresse figée** (`Get-Process ... .Modules`, recherche de
la plage base/fin contenant la RIP) : `0x00007FFBE8A00484` et
`0x00007FFBE8A00ED4` tombent **toutes les deux dans `ntdll.dll`**
(base `0x7FFBE88A0000`, offsets `0x160484`/`0x160ED4`) — c'est la couche
générique de transition syscall de Windows, empruntée par **toute**
primitive d'attente bloquante (`WaitForSingleObject`, sémaphores,
variables de condition SRW, timers, etc.), pas seulement l'audio. Sans
résolution de symboles (`dbghelp`/PDB) ou un vrai débogueur attaché, cette
adresse seule ne dit pas QUELLE primitive C# est en cause — seulement
qu'il s'agit d'un vrai blocage noyau, pas d'un spin natif.

**Bilan honnête de cette portion de l'investigation** : le mécanisme exact
reste non identifié à la fin de cette session. Ce qui est solide et
vérifié : (1) le thread OS réel derrière `ScreamWorker1`/`2` est
authentiquement bloqué en noyau, pas en boucle active ; (2) ce n'est pas
`WaveOutStream.Submit` ; (3) l'adresse est cohérente avec n'importe quelle
primitive de synchronisation .NET/Win32 standard.

## 7novies. `SdlHostAudio.Submit` (le vrai backend actif) ÉGALEMENT réfuté (2026-08-08, fin de session)

**Correction importante** : `WindowsWaveOutAudio` n'est **jamais instantiée**
— `WindowsHostPlatform.cs:16` et `PosixHostPlatform.cs:16` utilisent tous
les deux `new SdlHostAudio()`. Le §7octies testait donc, à raison, un
backend mort — mais il fallait vérifier le vrai. `SdlHostAudio.cs` a lui
aussi une boucle de contre-pression avec `Thread.Sleep(1)`
(`Submit`, ligne ~215, budget `MaximumWaitMilliseconds=250`), et surtout
**un diagnostic déjà existant, complet, jamais utilisé jusqu'ici** :
`SHARPEMU_LOG_AUDIO_QUEUE=1` → rapport `[PERF][AUDIO]` une fois par
seconde (`submits/s`, `fill%`, `blocked%`, `drops`, `empty`).

**Résultat** (`yotei_audioqueue_20260808_231220_stderr.log`, ~20 rapports
consécutifs pendant que `defer_owner_stuck` continuait de se déclencher
pour `ScreamWorker1/2` en parallèle) :
```
[PERF][AUDIO] stream#1 1,0s queued_ms min=0 avg=0 max=0-5 cap=683
  submits/s=25-39 fill=13-21% blocked=0% empty=25-38 drops=0
```
**`blocked=0%` sur tous les échantillons** — la boucle `Thread.Sleep(1)` de
`Submit` n'est quasiment jamais empruntée (la file SDL est vide ou
quasi-vide à chaque soumission, `cap=683ms` jamais approché). Débit de
soumission bas et sain (25-39/s), 0 `drops`. **`SdlHostAudio.Submit` est
donc également réfuté** comme site du blocage.

**Bilan final de la nuit sur ce sujet** : deux backends audio candidats
testés et éliminés par la mesure (pas par supposition). Le vrai site du
blocage `ntdll.dll` (offsets `0x160484`/`0x160ED4`, base
`0x7FFBE88A0000`) reste non identifié — soit il n'est pas dans le chemin
audio du tout (le nom `ScreamWorker` peut être trompeur), soit c'est un
tout autre appel à l'intérieur du pipeline audio (mixage, synchronisation
d'horloge `GuestAudioClock.cs`, ou une primitive HLE générique comme un
sémaphore/mutex non tracé). **Nécessite maintenant soit une résolution de
symboles ntdll (windbg/x64dbg attaché au process, ou `dbghelp` +
symboles Microsoft), soit un balayage plus large des primitives
`WaitOne`/`Wait`/`Monitor.Wait` dans tout `SharpEmu.Libs`/`SharpEmu.HLE`
avec le même genre de compteur déjà construit et validé deux fois ce
soir — la méthode fonctionne, il reste à l'appliquer au bon fichier.**

## 7decies. Chemin de repli `Monitor.Wait` de `scePthreadCondWait` également réfuté pour ScreamWorker (2026-08-08, fin de session)

**Piste testée** : `PthreadCondWaitCore` a un chemin de repli
(`KernelPthreadCompatExports.cs:1650`, `lock (state.SyncRoot) { while
(waiter.CompletionState == 0) Monitor.Wait(state.SyncRoot); }`) pour les
appelants **non coopératifs** (`cooperative = IsGuestThread &&
TryGetCurrentImportCallFrame(...)`, faux si l'appel ne vient pas
d'une exécution invitée normale). Un vrai `Monitor.Wait` bloque le thread
hôte synchroniquement — cohérent avec `ntdll` figé. Trace ajoutée
(`SHARPEMU_LOG_PTHREAD_CONDS=1`, réutilise le flag existant, aucun
changement de comportement), montrant `cooperative`/`is_guest_thread`/
`thread_handle` à chaque entrée dans ce chemin.

**Résultat** (`yotei_fallbackpark_20260808_231454_stderr.log`) : le chemin
de repli se déclenche bien (9 fois), mais **toujours avec
`is_guest_thread=False thread_handle=0x0`**, sur une seule adresse de
condvar fixe (`cond=0x0000000804C4BC58`) — un appelant **interne à
SharpEmu, côté hôte**, pas un thread invité, et certainement pas
`ScreamWorker` (qui a un vrai handle de thread invité). **0 occurrence**
avec `is_guest_thread=True` sur tout le run. **Cette piste est donc aussi
réfutée** pour `ScreamWorker` — ses appels `scePthreadCondWait` empruntent
toujours le chemin coopératif normal, jamais ce repli bloquant.

**Bilan des trois hypothèses testées et réfutées ce soir par la mesure,
pas la supposition** : `WindowsWaveOutAudio.Submit` (jamais appelée —
backend mort), `SdlHostAudio.Submit` (`blocked=0%` mesuré), et le repli
`Monitor.Wait` de `scePthreadCondWait` (jamais emprunté par un thread
invité).

## 7undecies. TROUVÉ — `ScreamWorker1/2` en spin-loop sur `scePthreadMutexLock`, pas en deadlock (2026-08-08, fin de session)

**Diagnostic décisif** : ajout de `last_import_nid`/`imports` (déjà
disponibles sur l'objet `GuestThreadState`, jamais exposés avant) à
`guest_threads.defer_owner_stuck` (`DirectExecutionBackend.cs`, même site
que §7octies). Aurait dû être la toute première chose loggée plutôt que de
tester des fichiers un par un.

**Résultat, run `yotei_lastnid_20260808_232412_stderr.log`**, une seule
série de mesures dans le temps :
```
deferrals=64    imports=234      last_import_nid=9UK1vLZQft4
deferrals=256   imports=18793    last_import_nid=9UK1vLZQft4
deferrals=512   imports=47558    last_import_nid=9UK1vLZQft4
deferrals=1024  imports=100675   last_import_nid=9UK1vLZQft4
deferrals=2048  imports=211756   last_import_nid=9UK1vLZQft4
```
**`9UK1vLZQft4` = `scePthreadMutexLock`** (`KernelPthreadCompatExports.cs:336`).
Et surtout : **`imports` grimpe rapidement et sans s'arrêter** (234 →
211 756 dans la même fenêtre d'observation) — **ce n'est pas un thread
figé/mort**, c'est un thread qui **exécute réellement des centaines de
milliers d'appels par seconde**, tous vers le même NID.

**Réconciliation avec toutes les observations de la nuit** :
`ScreamWorker1`/`2` sont dans une **boucle d'attente active (spin-wait) du
jeu lui-même**, pas dans un deadlock SharpEmu. Cohérent avec le
désassemblage du §7quinquies/7sexies (`0x800bafd20`–`0x800bafd3e`) : une
boucle qui relit `[rbx+4]`, appelle une fonction interne au jeu, et si la
condition n'est pas remplie, reboucle — cette fonction interne
verrouille/déverrouille un mutex à chaque itération, d'où l'avalanche
d'appels à `scePthreadMutexLock`. Le verrou lui-même se résout vite
(chemin rapide non contesté, jamais vu tomber dans le repli bloquant
`WaitForHostMutexLock` — §7octies/7novies le confirment), donc ce n'est
**pas le verrou qui coince** — c'est **le drapeau `[rbx+4]` que la boucle
attend qui ne devient jamais vrai**, exactement la même famille de bug que
le spin-flag `0x800D92942` déjà corrigé en tout début de nuit (recette
`SHARPEMU_FORCE_SPIN_FLAG_RIP`) — un **producteur manquant**, pas un verrou
cassé. `owner_rip` échantillonné dans `ntdll.dll` est cohérent avec des
centaines de milliers de courtes sections critiques `Interlocked`/`lock`
C# par seconde, pas avec un blocage permanent.

**Conséquence directe et vérifiable sur le son** : puisque cette boucle ne
sort jamais, le thread audio du jeu ne repasse jamais la main au vrai code
de mixage/soumission — expliquant à la fois l'absence de son pendant la
cinématique (rapportée par l'utilisateur) et la starvation généralisée du
dispatcher coopératif observée toute la soirée (le thread ne cède jamais
volontairement sa tranche d'exécution).

**Prochaine étape, la plus concrète de toute cette session** : identifier
la vraie adresse RIP où la boucle spin tourne (via
`SHARPEMU_FORCE_SPIN_FLAG_RIP`-style : capturer le RIP invité — pas l'hôte
— au moment du spin, en instrumentant `ExecuteBlockedGuestThreadContinuation`/
`ExecuteGuestThreadEntry` pour logger `context.Rip` à intervalle pendant que
`ScreamWorker` est actif) et le registre `rbx` au même instant pour calculer
`[rbx+4]`, puis déterminer quel événement du jeu (ou quelle fonctionnalité
manquante côté SharpEmu) devrait le faire passer à une valeur satisfaisante
— exactement la même méthode que celle qui a débloqué `0x800D92942` cette
nuit, appliquée à ce nouveau spin.

## 7duodecies. La capture du RIP invité échoue — ce N'EST PAS un spin natif comme `0x800D92942` (2026-08-08, fin de session)

**Diagnostic implémenté** : échantillonnage non throttlé (pas seulement
puissances de 2) du contexte hôte live pour tout candidat déjà à
`deferrals > 32`, ne loggant QUE si `guest_rip` tombe dans l'espace invité
(`0x800000000`–`0x900000000`) — silencieux sinon, donc coût mais pas de
bruit. Compile propre, republié.

**Résultat, run `yotei_guestrip_20260808_232748_stderr.log`** :
`defer_owner_stuck` (l'ancien diagnostic) a confirmé le blocage habituel
(`deferrals` jusqu'à 2048, `imports` jusqu'à 192 901, toujours
`last_import_nid=9UK1vLZQft4`), mais **`defer_owner_guest_rip` ne s'est
JAMAIS déclenché — zéro capture en espace invité sur des milliers
d'échantillons**. Contrairement au spin `0x800D92942` (facilement
attrapé en train de tourner en code natif invité), ce thread ne
repasse quasiment jamais par du code invité au moment où on
l'échantillonne — la quasi-totalité de son temps réel est authentiquement
côté hôte (.NET/`ntdll`), pas dans une boucle native du jeu.

**Vérification complémentaire** (`SHARPEMU_LOG_PTHREADS=1`, run bref de
~4s, 22 795 lignes `pthread_lock`) : les verrous tracés ne montrent
**aucune contention inter-thread** — `current` == `owner` sur toutes les
lignes examinées, `type=2` (mutex récursif), `recursion` oscillant
1→2→3→3→3 (ré-acquisitions récursives d'un même thread sur son propre
verrou, jamais une attente réelle). Le verrouillage lui-même est donc
rapide et non bloquant à chaque appel individuel — cohérent avec les
réfutations du §7octies/7novies (jamais tombé dans `WaitForHostMutexLock`
pour un thread invité).

**Bilan, honnête et final pour cette session** : le mécanisme exact reste
non identifié, mais son **profil** est maintenant beaucoup plus précis
qu'au début de la soirée : ce n'est ni un deadlock, ni un livelock
classique, ni un spin natif du jeu observable en RIP invité, ni une
contention de verrou. Le temps réel se perd quelque part entre des
centaines de milliers d'appels `scePthreadMutexLock`/`Unlock` rapides et
non contestés — soit dans un coût cumulatif de frais généraux légitimes
(très improbable à cette fréquence), soit dans un appel non tracé
distinct du verrouillage lui-même (une primitive de pacing/attente
audio réelle et bornée, auquel cas ce n'est peut-être pas un bug du tout
mais le comportement normal d'un thread de mixage audio actif — à
vérifier en écoutant si un son sort après la cinématique, pas seulement
pendant).

## 7terdecies. `dotnet-dump` (pas windbg) donne la vraie pile — et révèle un vrai bug SharpEmu séparé (2026-08-08, fin de session)

**Changement d'outil décisif** : `dotnet-dump` était déjà installé
(`dotnet tool list -g`). Comme SharpEmu est du code managé .NET, c'est le
bon outil — pas besoin de windbg pour les symboles natifs. Deux pièges
avant que ça marche :
1. Nécessite `DOTNET_ROOT`/`PATH` pointant vers `~/.dotnet` (sinon
   `hostfxr.dll` introuvable).
2. `dotnet-dump collect -p <pid>` sur le PID lancé par `Start-Process`
   dumpe le **process de lancement** (`TryRunMitigatedChild`), pas le
   vrai émulateur — SharpEmu lance un **processus enfant mitigé**
   (sécurité). Le vrai PID s'obtient via
   `Get-CimInstance Win32_Process | Where ParentProcessId -eq <pid_lancé>`.

**Résultat, dump du vrai processus enfant, `clrstack` sur les threads hôtes
de `ScreamWorker1`/`2`** :

```
ScreamWorker1 (host_tid=27320):
  System.Threading.Monitor.Enter_Slowpath   <- bloqué ici
  System.IO.TextWriter+SyncTextWriter.WriteLine(string)
  DirectExecutionBackend.TryYieldGuestThreadToHostStub
  DirectExecutionBackend.DispatchImport
  ...RunGuestThread

ScreamWorker2 (host_tid=23016):
  Interop+Kernel32.WriteFile   <- bloqué DANS le syscall d'écriture
  System.IO.StreamWriter.Flush
  System.IO.StreamWriter.WriteLine
  System.IO.TextWriter+SyncTextWriter.WriteLine(string)
  ...RunGuestThread
```

**Les deux threads se bloquent l'un l'autre à travers le writer synchronisé
de `Console`** : `ScreamWorker2` est en plein `WriteFile` (écriture réelle,
synchrone, vers le fichier de log redirigé), `ScreamWorker1` attend le même
verrou pour pouvoir écrire SA ligne. Vérification immédiate dans le propre
log du run : `guest_threads.claimed_for_execution` (log **inconditionnel**,
jamais gardé par un flag) apparaît **445 794 fois** sur 2,8 millions de
lignes totales — et `DirectExecutionBackend.cs:7265` confirme dans son
propre commentaire : *"Investigation-only: unconditional... "*, un log de
debug d'une session passée jamais nettoyé.

**Corrigé** (`DirectExecutionBackend.cs`, ~ligne 7262) : `claimed_for_execution`
gardé derrière `_logGuestThreads`, comme tous ses voisins. Compile propre,
republié, testé : le log ne dépasse plus les centaines de milliers de
lignes en boucle (comparé à des millions avant), confirmé par
`wc -l`. **Vrai bug de performance corrigé, indépendant de l'audio** — un
thread qui cède/reprend très souvent (n'importe lequel, pas seulement
`ScreamWorker`) aurait déclenché ce même I/O-thrashing.

**Mais le son et l'affichage restent absents après ce fix** (confirmé par
l'utilisateur). Le compteur `wake_key=pthread_mutex_waiter:N` de
`ScreamWorker1/2` continue de grimper très vite (313 886 dans le run de
test suivant) — la boucle de fond existe donc indépendamment du bug de
logging : ce n'était qu'un amplificateur, pas la cause racine.

**Désassemblage de `resume_rip=0x8002DBC92`/`0x8002DBCE8`
(guest, pas hôte cette fois)** — fonction complète `0x8002dbc40`–`0x8002dbd24` :
verrouille un mutex (`scePthreadMutexLock`), copie un chunk dans un buffer
circulaire via une boucle d'écriture bornée par `[rbx+8]`, déverrouille,
revérrouille, avance le pointeur d'écriture, déverrouille, retourne. **Code
de producteur audio parfaitement normal** — verrouillage rapide,
non contesté, aucune primitive de blocage à l'intérieur de cette fonction.
Rien à corriger ici.

**Conclusion structurelle** : cette fonction "pousse un chunk dans le
ring-buffer local" est appelée bien trop souvent (des centaines de
milliers de fois) pour un pipeline audio réel — et **ne contient elle-même
aucune pause/attente**. Le vrai régulateur doit être ailleurs : soit
l'appelant de cette fonction (pas encore identifié — recherche de xrefs
impossible sur ce binaire strippé sans plus d'outillage), soit le
mécanisme qui devrait créer une contre-pression quand le ring-buffer local
est plein (un sémaphore/event que `sceKernelWaitSema` ou équivalent devrait
honorer). Le `SdlHostAudio.Submit` réel (§7novies, `blocked=0%`) confirme
que rien en aval ne fait non plus obstacle — la chaîne entière tourne sans
jamais attendre nulle part, du producteur jusqu'au device.

**Bilan final de la session** : deux vrais bugs SharpEmu trouvés et
corrigés (retry-dispatch §7ter, logging inconditionnel §7terdecies) — tous
deux gardés, mesurés, aucune régression. Le blocage audio/affichage de
Ghost of Yotei lui-même n'est pas résolu, mais son périmètre est
maintenant précisément délimité : quelque part entre l'appelant de
`0x8002dbc40` et le moment où ça devrait bloquer sur de la vraie
contre-pression, cette contre-pression n'existe pas — la piste la plus
concrète pour la prochaine session.

## 7quaterdecies. Piste utilisateur — buffer d'affichage index=3 jamais dessiné (2026-08-09)

L'utilisateur rapporte (analyse indépendante, à vérifier comme toute
affirmation externe) : `0x5007190000` (slot d'affichage index=3) est bien
enregistré comme cible de flip par le jeu (`agc.display_buffer`), mais
`_guestImages.Add` n'est jamais appelé pour cette adresse — aucun draw ni
compute-writer ne cible jamais ce buffer, contrairement à `0x5005160000`
(index=2) qui fonctionne. Conclusion de l'utilisateur : pas un bug
d'enregistrement du flip, mais un 4ᵉ verrou/wait du même genre que ceux
corrigés ce soir, bloquant spécifiquement le chemin de rendu vers ce
second framebuffer.

**Vérifications faites ce soir** :
- `TraceDisplayBuffer` (`AgcExports.cs:7464`) ne se déclenche que sur un
  vrai FLIP (pas à l'enregistrement) — dans le run de test de cette
  session (`yotei_buf3_20260808_235943_stderr.log`, 172k lignes), **seul
  index=2 a été vu flippé**, index=3 jamais atteint dans la fenêtre
  observée. Cohérent avec le constat de l'utilisateur sans le reproduire
  directement — probablement une question de durée d'observation (voir
  ci-dessous).
- `ExecuteOrderedGuestFlipWait` (`VulkanVideoPresenter.cs:6202`) — lu en
  entier : c'est un marqueur **purement informatif** (log seulement,
  aucun blocage réel). **Écarté** comme mécanisme de blocage.
- `agc.wait_suspended` avec `producer=none-observed` (9 occurrences,
  files `acb.compute[48/56/72]`) — semblaient prometteurs mais **tous
  résolus correctement** en quelques centaines de ms via le mécanisme
  orphan-preamble déjà corrigé ce soir (`agc.dcb.release_mem wrote=True`
  puis `agc.queue_resumed` confirmés par grep). **Pas le blocage
  cherché.**

**Hypothèse de travail, non vérifiée** : les deux problèmes non résolus
de la session (le spin audio `ScreamWorker` et le second framebuffer
jamais dessiné) sont peut-être **liés par le débit, pas par un mécanisme
commun** — le spin audio consomme suffisamment de temps CPU/scheduler
coopératif pour que le jeu n'atteigne tout simplement jamais, dans une
fenêtre d'observation de quelques dizaines de secondes à quelques
minutes, le point où il tente de dessiner dans le second framebuffer.
Rien ne prouve encore une dépendance causale directe entre les deux ; à
confirmer avec soit un run beaucoup plus long, soit après avoir résolu le
spin audio (§7terdecies) pour voir si le second buffer se met à recevoir
des draws une fois le débit normal restauré.

**Non résolu à la fin de cette session.**

## 7quindecies. Le fix logging aide le débit mais NE règle PAS le spin `ScreamWorker`, ni `FaWorkerIo1` (2026-08-09)

**Run de 2 minutes** (`yotei_audioduring_20260809_000933_stderr.log`, 869k
lignes, `SHARPEMU_LOG_AUDIO_QUEUE=1` + `SHARPEMU_LOG_GUEST_THREADS=1`) :

- `SdlHostAudio.Submit` : **`blocked=0%` sur toute la durée** — confirme
  (ne réfute pas cette fois, mesuré *pendant* une longue fenêtre) que la
  contre-pression décrite dans l'analyse de l'utilisateur (Kyty
  `target_latency_us=40000` + purge à 200ms) **n'est pas le mécanisme en
  cause ici** — SharpEmu ne bloque jamais dans `Submit`, la file ne se
  remplit jamais assez pour ça.
- `ScreamWorker2` continue son spin sur `scePthreadMutexLock`
  **(1 406 306 imports** sur ce seul run, plus que les runs précédents,
  pas moins) — le fix logging (§7terdecies) a réduit le bruit de log et
  amélioré le débit global (61 draws/s rapportés par l'utilisateur,
  record de la nuit), mais **n'a pas touché à la boucle spin
  elle-même**.
- **`FaWorkerIo1` reste figé exactement comme en §7quinquies** — 33
  reprises, `resume=0x0000000800BAFD9B` **à chaque fois**, jamais un autre
  point de reprise. Identique au comportement documenté avant tous les
  fixes de cette session.

**Conclusion révisée** : le vrai goulot pour "0 fps" côté jeu (rapporté
directement par l'utilisateur, pas une métrique SharpEmu) est
probablement `FaWorkerIo1`, pas `ScreamWorker` — il n'a strictement pas
bougé depuis sa toute première observation, avant même le fix
retry-dispatch. Le spin `ScreamWorker` sur le mutex reste un vrai
problème de performance à part (piste de l'utilisateur : aligner
`SdlHostAudio.Submit` sur la politique Kyty reste probablement une bonne
idée pour la robustesse générale, même si ce n'est pas ce qui bloque
`ScreamWorker` *ici* puisque `blocked=0%`), mais **`FaWorkerIo1` est la
piste prioritaire** pour la prochaine session : même adresse de reprise
figée que la toute première nuit, jamais résolue par aucun des fixes
appliqués depuis.

## 7sexdecies. `FaWorkerIo1` DISCULPÉ — le condvar fonctionne correctement (2026-08-09)

**Question du §7sexies enfin tranchée**, exactement comme l'utilisateur l'a
posée : trace ajoutée sur `PthreadCondSignalCore` + `TryGrantCondWaiterMutex`
+ `PthreadCondWaitCore`, filtrée sur l'adresse du condvar de `FaWorkerIo1`
(`0x804C4BC40`, vérifiée **stable sur 2 runs différents** avant de filtrer
dessus). Nouveau flag `SHARPEMU_LOG_PTHREAD_COND_FILTER=<hex>`, coût nul
en dehors de l'adresse filtrée. Compile propre.

**Résultat** (`yotei_condfa_20260809_001931_stderr.log`), séquence complète
et sans ambiguïté :
```
cond_trace.wait-enter   thread=...D0  wake_key=1
cond_trace.grant-check  result=not-ready completion_state=0
cond_trace.signal       completed_this_call=1        <- LE SIGNAL ARRIVE
cond_trace.completed    thread=...D0
cond_trace.grant-check  result=denied  mutex_owner=...20F0   <- un AUTRE thread tient le mutex
cond_trace.grant-check  result=granted mutex_owner=...D0     <- ré-essai, accordé
cond_trace.wait-enter   thread=...D0  wake_key=3      <- reboucle aussitôt
```
**Le condvar EST signalé à chaque cycle** (`completed_this_call=1`,
jamais 0) — ce n'est **pas** un producteur manquant. `FaWorkerIo1` obtient
le mutex après 1-2 refus normaux (contention transitoire avec un autre
thread, résolue en quelques micro-attentes), fait un peu de travail, et
revient dans `cond_wait` — **un cycle producteur/consommateur parfaitement
sain**, juste très fréquent. `resume=0x0000000800BAFD9B` identique à
chaque fois n'indique pas un blocage : c'est simplement le même site
d'appel `cond_wait`, atteint normalement à chaque itération d'une boucle
de traitement de file (I/O) qui tourne correctement.

**`FaWorkerIo1` est donc disculpé.** L'hypothèse "figé depuis le début"
(§7quindecies) était fausse — confirmé par la mesure, pas par supposition,
exactement la méthode que ce document impose. Retour à la case
`GHOST_OF_YOTEI_INVESTIGATION.md` §7bis/§7quaterdecies (le second
framebuffer/G-buffer jamais rempli) comme **seul candidat restant, non
réfuté**, pour l'absence d'affichage.

## 7septdecies. Le son — PISTE FERMÉE : le pipeline est sain, le contenu PCM est du silence pur (2026-08-09)

**Diagnostics déjà existants dans le code, jamais activés ensemble** :
`SHARPEMU_LOG_AUDIO_OUT=1` (`AudioOutExports.cs:29`, API `sceAudioOut`) et
`SHARPEMU_LOG_AUDIO_OUT2=1` (`AudioOut2Exports.cs:1237`, API
`sceAudioOut2`) — chacun trace un `peak` d'amplitude calculé sur les
échantillons PCM juste avant soumission au backend. Lancés ensemble,
aucune nouvelle instrumentation nécessaire.

**Résultat** (`yotei_peak_20260809_112225_stderr.log`) : seule l'API
`sceAudioOut2` est utilisée par le jeu (0 ligne `audioout.output#`, l'API
`sceAudioOut` n'est jamais appelée). **Sur toutes les soumissions
observées (14 échantillonnées jusqu'à #500+, confirmées vivantes jusqu'à
au moins #3000 avant l'arrêt manuel)** :
```
audio_out2.context-submit#N handle=0x3 frames=256 ports=6 peak=0,0000 backend=sdl3-primary
```
**`ports=6` à chaque fois** — 6 ports audio actifs sont bien résolus,
`port.PcmAddress` non nul, lecture mémoire réussie (`ctx.Memory.TryRead`
doit réussir pour que `mixedPorts` s'incrémente) — donc **l'infrastructure
croit avoir de vraies données**. Mais **`peak=0,0000` sans exception, sur
un run entier** : les échantillons lus depuis la mémoire invitée sont
authentiquement silencieux — zéro partout, tout le temps.

**Conclusion, ferme celle-ci** : ni la piste Kyty de l'utilisateur
(contre-pression `SdlHostAudio.Submit`) ni aucun mécanisme de blocage de
thread ne peuvent expliquer ceci — la chaîne de soumission fonctionne
(confirmé §7quindecies/7sexdecies, `blocked=0%`, condvars signalés
normalement). Le problème est **en amont** : soit le jeu n'a pas encore
commencé à écrire du contenu audio réel dans les buffers PCM (état de
jeu qui n'a jamais réellement démarré, cohérent avec le G-buffer vide en
§7bis), soit SharpEmu lit au mauvais endroit/mauvais format (moins
probable — improbable que 6 ports différents soient tous mal interprétés
de façon identique).

**Diagnostic unifié de fin de session** : trois sous-systèmes indépendants
(rendu du second framebuffer, mixage audio, `ScreamWorker`/`FaWorkerIo1`)
tournent tous **correctement en tuyauterie** mais **sans jamais recevoir
de contenu réel** — un G-buffer jamais rempli, un ring-buffer audio jamais
alimenté en vrais échantillons. Le dénominateur commun n'a pas été trouvé
cette nuit, mais le périmètre est maintenant précis : chercher ce qui,
côté jeu, devrait déclencher le passage de "chargement/attente" à
"simulation réelle qui produit du contenu" — pas une primitive de
synchronisation SharpEmu de plus, un état de plus haut niveau côté jeu.

## 7octodecies. Hypothèse "dénominateur commun avec le livelock Tsushima" TESTÉE ET RÉFUTÉE (2026-08-09)

Contexte : la même session a trouvé et corrigé un vrai bug d'encodage dans
`CreateExceptionHandlerTrampoline` (`DirectExecutionBackend.cs` ~3013/~3096) —
un octet REX faux (`0x4C` au lieu de `0x4D`) faisait exécuter
`lock cmpxchg [rcx], r10` au lieu de `[r9], r10` dans le spinlock récursif
`_vehManagedEntryLock`, cassant totalement son exclusion mutuelle (livelock
infini confirmé sur Ghost of Tsushima). Hypothèse testée ici : ce même bug,
partagé par le chemin host ET guest de ce verrou, pourrait-il être le
dénominateur commun avec le stall Yotei ?

**Test** : build avec le fix appliqué, relancé avec la recette confirmée
(`SHARPEMU_FORCE_SPIN_FLAG_RIP=0x800D92942` + `SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES=1`
+ `SHARPEMU_LOG_AGC=1`), ~130s de run (`yotei_vehfix_20260809_170819_stderr.log`,
577k lignes).

**Résultat : comportement identique, au bit près, à l'état déjà documenté en
§7quinquies (2026-08-08, avant le fix)** :
- `FaWorkerIo1` : `imports=608` figé sur tout le run (`grep -c "name='FaWorkerIo1'"`
  → dernières lignes toutes `imports=608 progressed=False`), **exactement** la
  valeur déjà vue en §7quinquies.
- `Stall main-thread-os` : `live_rip` oscille entre `0x0000000800CD5002` et
  `0x0000000800CD5008`, `live_rsp=0x00007FFFF01FE370` **identique** sur les 16
  échantillons du run — même adresse, même RSP que §7quinquies (`0x0000000800CD5002`
  / `0x00007FFFF01FE370`), à un TID hôte différent près (attendu, ré-attribution
  du pool d'exécuteurs).
- 1 seule frame présentée (`presented guest frame`), cohérent avec un run qui
  rejoue la cinématique puis se re-bloque au même endroit qu'avant.

**Conclusion : hypothèse réfutée.** Le fix du verrou VEH ne change rien
d'observable au comportement de Yotei — le run retombe très exactement dans
l'état déjà caractérisé et disculpé en §7sexdecies (le condvar de `FaWorkerIo1`
est signalé normalement ; `imports=608` figé est un artefact de comptage, pas
un vrai blocage). Le stall Yotei et le livelock Tsushima sont donc deux bugs
distincts malgré la proximité du code (`CreateExceptionHandlerTrampoline`
partagé) — pas de fix unique. Le vrai bloqueur Yotei reste ce que §7septdecies
a identifié : un état de plus haut niveau côté jeu jamais atteint, pas une
primitive de synchronisation SharpEmu.

### §7octodecies — test du fix VEH sur Yotei (130s, recette confirmée)

Run de 130s avec le fix Tsushima appliqué + `SHARPEMU_FORCE_SPIN_FLAG_RIP=0x800D92942`
et `SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES=1`. Résultat **identique au bit près**
à l'état documenté le 2026-08-08 (avant le fix) :

- `FaWorkerIo1` : `imports=608` figé — même valeur que §7quinquies.
- `Stall main-thread-os` : `live_rip` oscille entre `0x800CD5002`/`0x800CD5008`,
  `live_rsp=0x7FFFF01FE370` — identique à l'ancien relevé.
- 1 seule frame présentée sur tout le run.

Le fix Tsushima reste valable et à committer **indépendamment** — il règle le
livelock Tsushima, mais n'est pas un fix Yotei. Ne pas re-chasser cette piste
sur Yotei sans nouvelle preuve.

---

## 7novemdecies. Comparaison Tsushima/Yotei — pas de correctif commun démontré (2026-08-10)

Une nouvelle lecture passive du chemin Tsushima qui avait été nommé
`GameState` a écarté cette sous-piste. La fonction invitée
`0x80070BCE0` compare une capacité disponible (`0x87800`) à un besoin
(`0x7551`) et retourne le statut `2` lorsque la capacité est suffisante.
Son dispatcher ne fait que propager ce statut au caller ; il ne bifurque pas
vers un chemin de rendu. Cette observation vient du log
`artifacts/tsushima-gamestate-consumers_stderr.log` et ne permet donc pas de
qualifier `GameState` comme la cause de l'écran noir.

Conclusion transversale actuelle : les deux jeux finissent avec des buffers
de présentation vides, mais les preuves ne donnent **pas** le même mécanisme
en amont. Tsushima n'émet pas de draws de géométrie dans la fenêtre observée ;
Yotei exécute une partie de son pipeline mais ne remplit pas son framebuffer
de contenu et ses buffers PCM restent silencieux. Les corrections AGC, VEH,
SaveData et les stubs input ne doivent pas être modifiés à nouveau sur la
seule base de ce symptôme commun.

Prochaine mesure utile : tracer, dans Yotei, le premier producteur qui devrait
faire passer les buffers audio/G-buffer de zéro à du contenu, puis remonter au
contrat HLE qui décide cette transition. Tout correctif doit être testé sur les
deux jeux et sur au moins un titre déjà fonctionnel.

Mesure ciblée du 2026-08-10 : `SHARPEMU_TRACE_JOBWORKER_HLE=1` donne 651
appels HLE depuis les `JobWorker`, tous résolus et `ORBIS_GEN2_OK`. Ils
émettent notamment de vraies commandes AGC (`sceAgcDcbAcquireMem`,
`sceAgcDcbEventWrite`, `sceAgcDcbDrawIndexAuto`) puis attendent les
sémaphores `0x2` et `0x3`. Un second relevé global a confirmé que ces deux
sémaphores sont bien signalés depuis plusieurs retours invités ; ce n'est pas
un signal HLE manquant. La prochaine cible est donc l'origine des soumissions
JobManager qui s'arrêtent après le burst de démarrage, et non l'implémentation
de `sceKernelWaitSema`/`sceKernelSignalSema`.

Le désassemblage passif du 2026-08-10 précise la frontière :
`0x800FF5620` pousse systématiquement un job neuf (`state = -1`) dans le
conteneur JobManager. Il ne comporte aucune condition qui désactive le rendu.
La factory `0x800FF57C0` est appelée indirectement : une lecture seule de
l'EBOOT ne trouve ni appel `rel32` ni pointeur absolu vers cette adresse.
L'arrêt des jobs se situe donc dans le producteur/callback dynamique qui doit
appeler cette factory. Le traceur de breakpoints JobManager n'a pas atteint
ce callback pendant son court échantillon ; ce résultat est négatif et ne
justifie aucun changement de comportement.

Correction de l'instrumentation (opt-in seulement) : le producteur s'exécute
sur le thread d'entrée principal, alors que les breakpoints JobManager étaient
armés uniquement dans les workers natifs. Une fois armés aussi à l'entrée,
le run a capturé cinq soumissions entre `t=0,7s` et `t=1,2s`, puis aucune.
Les factories nommées observées sont `lang_english_text` et `pulse.sprig`.
Leurs callers testent le retour de `0x800BAF900` et le flag `0x4000` avant
d'appeler la factory ; pendant le burst, le retour est bien zéro et les jobs
sont donc acceptés. `0x800BAF900` est un ordonnanceur natif qui crée/enfile
une opération interne, pas un export HLE. Le fait déterminant est que les
producteurs eux-mêmes ne sont plus rappelés après le burst : il ne faut pas
forcer leur soumission ni altérer JobManager/AGC.

### §7vicies. Tsushima — correctif EqEvent validé, AGC vivant mais sans passe UI (2026-08-10)

Le contrat de `sceAgcDriverGetEqEventType` a été rétabli selon Kyty : pour un
kevent graphics (`filter=-14`), le type est `ident` et le contexte est `data`.
Les trois tests `AgcEqEventDecodeTests` passent et un run du binaire Win-x64
ne produit plus le warning `returning 0`. C'est un correctif ABI général, pas
un forçage de rendu.

Il ne suffit toutefois pas à faire apparaître Tsushima. Un run AGC détaillé
atteint plus de 630 soumissions DCB/ACB à `t=13,4s`; les waits de
`acb.compute[72]` d'abord signalés `producer=none-observed` sont bien associés
quelques millisecondes plus tard à leurs `release_mem`, puis repris. Cette
queue est donc fonctionnelle et ne doit pas être forcée. Malgré cela, le run
ne contient que 11 `agc.shader_draw`, tous vers les trois targets noires déjà
connues, et une seule présentation. L'EventFlag du thread principal est
également signalé puis consommé correctement. Le blocage restant est en amont
de la construction de la passe calibration/UI, pas dans AGC, les waits ou le
réveil EventFlag.

### §7unvicies. Yotei — gel des soumissions DCB caractérisé, producteur GPU toujours absent (2026-08-10)

Le run passif `artifacts/yotei-dcb-freeze_stderr.log` active seulement
`SHARPEMU_WATCH_DCB_SUBMIT=1`. Trois secondes après la dernière soumission,
le watcher a relevé **un seul** appel `sceAgcDriverSubmitDcb` depuis le boot,
puis aucune nouvelle soumission. Une frame noire est présentée avant ce gel.

- Trois queues compute restent suspendues sur les labels
  `0x2011831650`, `0x2011882330` et `0x2011669FE0`, chacun à `cur=0`,
  `ref=1`.
- Le DCB graphique finit également sur une attente synthétique de queue-tail
  à `0x201162C3EC` (`cur=0`, attente de mot non nul). Le log AGC précédent
  montre explicitement `agc.dcb.ring_tail_pending` à cette adresse.
- Le scan des arènes de builders ne trouve **aucun** packet `release_mem` ou
  `write_data` visant les trois labels compute. Leur producteur n'est donc ni
  construit hors curseur ni caché derrière une attente antérieure dans les
  arènes connues : il n'est pas produit dans cet état du jeu.
- Les `JobWorker` sont tous bloqués de façon normale sur
  `sceKernelWaitSema`; aucun ne porte un échec HLE. Le dernier import observé
  du thread principal est `sceAgcDriverSubmitDcb`, ce qui est un **historique**
  et non la preuve qu'il reste bloqué dans cet export : le code de
  `DriverSubmitDcb` retourne immédiatement après avoir enregistré la
  soumission.

Conclusion : le clear zéro du buffer composite est une conséquence directe de
l'unique DCB de boot. Le correctif ne consiste pas à forcer les waits ou les
JobWorkers : il faut identifier pourquoi le jeu ne construit plus la
soumission suivante, ou quel contrat HLE manque avant cette transition. Ce
résultat ne prouve toujours pas une cause commune avec Tsushima.

---

## 8. Vérification des affirmations

Toute affirmation tirée d'un log doit citer le fichier et être confirmée par
`grep` avant d'être qualifiée de fait. Plusieurs analyses de ce dossier ont
été invalidées par des citations non vérifiées (label fantôme, `cmp=4`,
comptages sous béquille). Distinguer explicitement fait mesuré et hypothèse.
