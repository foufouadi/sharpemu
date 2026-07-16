# Ghost of Yōtei — boot vers le menu (branche fix/yotei-boot-deadlock)

## 2026-07-17 — Session en cours

### État observé (preuves, pas de mémoire)

Le blocage actuel n'est **pas** le gel FaWorkerIo1 : c'est un **crash natif avant
toute présentation** (exit 139 = AV 0xC0000005 non rattrapé, banner
`NATIVE EXCEPTION CAUGHT` dans le log). Aucune activité VideoOut/flip dans les
logs des runs actuels — le splash n'est plus atteint. L'assert Scream était déjà
présent hier soir (`log_yotei_snap4.txt`, 2 occurrences) : ce n'est pas une
régression d'aujourd'hui.

Exit codes vérifiés dans le source (règle 6) :
- `4`  = watchdog de stall, `DirectExecutionBackend.cs:5666` (« Execution stalled with no import progress »)
- `0`  = fermeture fenêtre VideoOut (`VideoOutExports.cs:166`)
- `139` = AV natif (128+11 côté shell), correspond au banner 0xC0000005
- `6`  = absent de notre code — vraisemblablement un `exit(6)` du jeu via HLE

### Étape 0 — A/B régression

- **Run (a)** HEAD c9fd4fa + stubs non commités (ACM batch, AJM batch,
  GetQueueLevel headroom) : ×2 runs (`log_yotei_menu1.txt`, `log_yotei_menu2.txt`).
  Point d'arrêt reproductible : import #29549 (#29033 au 2e run, même site
  `ret=0x8002561F4`), assert Scream `snd_osfuncs.cpp:408`
  « scePthreadMutexUnlock FAILED w/ error -2147352573 » (= 0x80020003,
  ORBIS_GEN2_ERROR_INVALID_ARGUMENT), puis AV à RIP 0x80025621F (lecture
  0xFFFFFFFFFFFFFFFF dans le chemin d'erreur du jeu) → exit 139.
  Aucun flip/present avant le crash.
- **Run (b)** idem avec les stubs audio non commités stashés (`log_yotei_ab_b.txt`) :
  **gel** (pas de crash) — dernier import logué #23858, log figé à 12 864 lignes,
  1 076 s de CPU consommées en 18 min = busy-spin. Spam EDEADLK sur le mutex
  connu 0x804C4B9B8 (chemin FaWorkerIo1). Aucun flip/present. Même 6 échecs
  create_shader type=5.

**Verdict A/B : on garde (a).** Les stubs audio font passer le mur de la pompe
NCA (~#23858 → ~#29549, ~6 000 imports de plus). La perte du splash n'est pas
imputable aux stubs : aucune des deux configs ne présente de frame, et l'assert
Scream existait déjà dans `log_yotei_snap4.txt` d'hier soir.

### Étape 2 — Diagnostic écrit du crash (cause racine identifiée, trace à l'appui)

Trace `log_yotei_menu2.txt` (SHARPEMU_LOG_PTHREADS=1), séquence finale sur le
thread principal (managed=2) :

```
pthread_lock:   mutex=0x7FFFF01FF890 resolved=0x600000008780  guest[0]=0x600000008780  rec=1 OK
pthread_unlock: mutex=0x7FFFF01FF898 resolved=0x7FFFF01FF898  guest[0]=0x600000008780  rec=0 OK
pthread_unlock: mutex=0x7FFFF01FF898 resolved=0x7FFFF01FF898  guest[0]=0x6000000088C0  rec=0 result=0x80020003  ← ÉCHEC
```

- Scream (`snd_osfuncs.cpp`) copie les **handles** de mutex (`ScePthreadMutex`,
  8 octets) dans des locales de pile : le même slot de pile (0x7FFFF01FF898)
  est réutilisé d'un appel à l'autre avec des handles **différents**
  (0x600000008780 puis 0x6000000088C0).
- `TryResolveMutexState` (KernelPthreadCompatExports.cs:916) consulte le cache
  `_mutexStates` **indexé par adresse** avant de relire le handle en mémoire
  guest ; l'entrée périmée `0x7FFFF01FF898 → état(8780)` gagne alors que le slot
  contient désormais 88C0. Le mutex 88C0 est bien verrouillé (rec=1, cf. trace
  `lock mutex=0x7FFFF01FF910 resolved=0x6000000088C0`), mais l'unlock est routé
  vers l'état de 8780 (rec=0) → INVALID_ARGUMENT.
- Le jeu traite l'échec comme fatal : assert + chemin d'erreur qui AV.

Sur hardware réel, l'identité d'un mutex est le handle contenu dans l'objet,
jamais l'adresse du pointeur. Fix minimal : résolution « contenu d'abord »
(lire guest[0] ; si le handle est enregistré, l'utiliser ; l'adresse ne sert
que de repli), même réordonnancement dans `ResolveMutexHandle`.

Le même motif de cache existe pour `_condStates` (cond vars) — non observé en
échec, non touché (une cause, un fix).

### Blocage secondaire connu (non traité dans ce fix)

`sceAgcCreateShader` (f3dg2CSgRKY) rejette les shaders **type=5 (hull shader)** :
registres PGM `lo=0x10A hi=0x10B` absents de la table de
`PatchShaderProgramRegisters` (AgcExports.cs:10025). Motif cohérent avec les
constantes existantes (PS=0x8, GS=0x8A, ES=0xC8, LS=0x148 → HS=0x10A).
6 échecs par run, **non fatals** (le jeu continue). Prochaine hypothèse après le
fix mutex.

### Étape 3 — Fix appliqué et vérifié

Fix : résolution « contenu d'abord » dans `TryResolveMutexState` et
`ResolveMutexHandle` (KernelPthreadCompatExports.cs) — le handle lu dans
l'objet guest prime sur le cache indexé par adresse ; l'entrée d'adresse est
rafraîchie à chaque résolution par contenu.

Run de vérification (`log_yotei_mutexfix1.txt`, SHARPEMU_LOG_PTHREADS=1) :
- 0 échec `pthread_unlock`, 0 assert Scream (avant : crash reproductible ×2) ;
- progression #29549 → **#32415+** (~3 000 imports plus loin) ;
- tests unitaires mutex/pthread : 2/2 OK, build 0 erreur.

### NOUVEAU MUR (documenté, non traité — arrêt conformément au protocole)

Exit 139, AV **écriture à NULL** à RIP 0x807567218 = `Q3VBxCXhUHs+0x648`,
une fonction de la **libc bundlée** (LLE redirect logué au boot :
`0x0000700000001970 Q3VBxCXhUHs -> 0x807566BD0`). Registres au crash :
rdi=0 (destination NULL), rdx=0x24A, rax=0 — allure de memset/memcpy vers NULL.

Chaîne en amont (thread principal, même fonction du jeu 0x801612xxx) :
1. Import#32415 `6lNcCp+fxi4` appelé avec **rdi=0 rsi=0** rdx=0xE0008000 →
   INVALID_ARGUMENT (ret=0x80161279F) — le jeu a DÉJÀ un pointeur/valeur nul ici.
2. Retour à 0x8016127C4 (rsp+0x08) → appel libc `Q3VBxCXhUHs` avec le même
   NULL → AV.

Le NULL vient donc d'encore plus tôt. Candidats observés dans le même run
(à instrumenter, PAS à stubber à l'aveugle) :
- Import#32315 `Q4qBuN-c0ZM` → -2143223530 (0x80430016) (ret=0x800F98375)
- Import#31749/31757 `rqwFKI4PAiM` (rdi=0xFFFFFFFF) → NOT_FOUND (ret=0x800085779)
- `6lNcCp+fxi4`, `Q4qBuN-c0ZM`, `rqwFKI4PAiM`, `Q3VBxCXhUHs` : absents de
  scripts/ps5_names.txt — à résoudre via le workflow capstone offline
  (scratchpad `yotei_dis.py`) en désassemblant les call-sites (ret-addresses
  ci-dessus) dans l'eboot déchiffré.

### Blocage tertiaire connu (inchangé)

`sceAgcCreateShader` type=5 (hull shader) : 6 échecs/run, non fatals — voir
section plus haut. Hypothèse prête (case 5 → lo=0x10A hi=0x10B).

### Prochaine hypothèse (pour la prochaine session)

Identifier lequel des appels échouants en amont produit le pointeur NULL
consommé à 0x80161279F/0x8016127C4 : désassembler la fonction 0x801612xxx
autour des deux call-sites pour voir d'où vient rdi, puis remonter.
