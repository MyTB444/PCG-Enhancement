# Library of the Magus

**ECS7016P Programming Assignment — 2D Level Generator**

Based on Sebastian Lague's *Procedural Cave Generation* tutorial. All new code is in `Assets/Scripts/MapGenerator.cs` and is marked with `// NEW CODE` comments. Stretch-goal code is in `Assets/Scripts/Agents/` and `Assets/Scripts/Evaluation.cs`.

---

## Concept

**Title:** *Library of the Magus*

**Theme:** The player is Aldric, a young scholar who inherits a rusted key and a cryptic note from his late grandfather, the reclusive mage Erasmus: *"See you in my quarters."* The key unlocks the entrance to his grandfather's long-sealed tower, a vast labyrinth of interconnected library chambers, each containing two ancient portals that transport the player to a random room deeper in the tower.

**Gameplay & environments:** Aldric must explore the chambers, collect scattered spell-books and journal fragments that reveal his grandfather's secrets, and navigate the unpredictable portal network to reach the personal quarters hidden at the heart of the tower. Each generated level represents one chamber of the tower: a stone-walled room with crumbling architectural features, containing two portals (the exits) to other chambers.

**Interactions:** The player picks up books and journal fragments scattered in each chamber. Spectral librarians drift between the shelves but retreat when approached. Stone guardians patrol the chambers and pursue the player on sight. A magical wisp accompanies the player, drifting toward landmarks that the player might otherwise miss.

---

## Generator Design

The generator modifies Stages 1 and 3 of Lague's pipeline. Stage 2 (cellular automata smoothing) is unchanged. (Make sure random seed is true on map generator!)

### Stage 1 — BSP (Binary Space Partition)

Lague's original uses a weighted random coin-flip per cell, producing undifferentiated noise. I replaced this with Binary Space Partition, a classical dungeon-generation technique (Shaker, Shaker & Togelius, *Procedural Content Generation in Games*, Springer 2016, Chapter 3).

The map begins fully solid. The playable area is recursively split into smaller rectangles, alternating between horizontal and vertical cuts, preferring to split the longer axis to keep leaves roughly square. Splitting stops when a leaf falls below `minLeafSize`. Inside each final leaf, a room is carved by shrinking the leaf inward by `roomPadding` on all sides.

This produces a grid of distinct rooms separated by walls. The cellular automata smoothing in Stage 2 then erodes the sharp rectangular edges, giving the rooms an overgrown, weathered look. The result is levels that feel both structured (architectural rooms) and organic (weathered by time), fitting the concept of an ancient mage's tower reclaimed by magical decay.

Parameters: `useBSP`, `minLeafSize`, `roomPadding`.

### Stage 3 — Post-Processing

Lague's Stage 3 removes small regions and connects rooms with fixed-width corridors (`ConnectClosestRooms` → `CreatePassage`). The original logic is kept. Four new post-processing passes are added, plus one modification to `CreatePassage`:

**`SmoothPassageEdges`** — one-pass erosion that removes wall cells with fewer than 3 wall neighbours, softening the jagged edges that `DrawCircle` leaves along corridor walls. Reads from a cloned grid to prevent cascading.

**`CreateExitRoom`** — places exactly two square exit rooms (the "portals") in the two non-main rooms furthest from the main room, with a minimum separation of one-third of the map diagonal to prevent them clustering. Each exit is a sharp-walled rectangle with 7-cell doorways on all four sides. Positions are clamped to guarantee the structure fits; fallback positions at opposite map corners are used if no candidate rooms qualify. Exit centres are stored so the player can spawn inside one.

**`CreateClearings`** — for each room above `clearingMinRoomSize`, carves a circular clearing at the room's centroid. The main room gets a larger clearing, indicating where the player spawns. Rooms whose centroid falls near an existing exit are skipped to avoid damaging exit walls.

**`PlaceLandmarks`** — places at most one 3×3 wall block (a "column") per room, in a position with a clear 7×7 area. Half the rooms are skipped randomly. Tiles inside or near an exit are excluded. Columns represent ruined architecture in the overgrown library.

**`CreatePassage` modification** — passage width varies by connection importance: main-room connections use `maxPassageWidth`; connections between two already-connected rooms use the midpoint; all other connections use `minPassageWidth`. This creates a visible hierarchy of main corridors vs. side paths.

**`SpawnPlayer` modification** — the player now spawns at the centre of the first exit room rather than a random empty tile.

### Pipeline order

The order of Stage 3 calls matters:

```
ConnectClosestRooms → SmoothPassageEdges → CreateExitRoom → CreateClearings → PlaceLandmarks
```

Smoothing runs first so it can't erase any structures placed later. Exits run before clearings and landmarks so those can check exit positions and exclude tiles that would overlap.

---

## Stretch Goal: Agents

Three agent types are implemented in `Assets/Scripts/Agents/`. All three use a finite-state-machine acting as a simple behaviour tree, and are spawned automatically on every regeneration by `Assets/Scripts/AgentSpawner.cs`.

**LibrarianAgent** — a spectral spirit of a past reader. Three states: **Idle** (wait), **Drift** (slow movement toward a random empty tile), and **Fade** (flee from the player when they come within `fadeDistance`). The librarian represents the timid ghosts haunting the tower: they avoid the player and react with movement rather than aggression.

**GuardianAgent** — a stone automaton bound by Erasmus to protect the library. The most complex agent, with three behaviour layers and two distance thresholds. **Patrol** is the default, walking between patrol points generated at spawn from random empty tiles. When the player enters `alertDistance` the guardian switches to **Investigate**, moving at increased speed toward the player's last known position. When the player enters `chaseDistance` the guardian switches to **Chase** and sprints directly at the player. Losing sight during a chase demotes the guardian back to Investigate (searching the last known position for `investigateTimeout` seconds) before returning to patrol. This alert-engage-disengage cycle mirrors classic stealth-game sentries.

**WispAgent** — a tiny enchanted light that accompanies the player. Three states: **Follow** (orbits the player at `followDistance`), **Point** (drifts toward a nearby landmark within `landmarkScanRadius` to draw the player's attention), and **Catchup** (fast movement toward the player if the wisp falls outside `leashDistance`). The Point state queries the current map for 3×3 column landmarks by looking for wall cells with ≥ 4 wall neighbours, matching the signature left by `PlaceLandmarks`. This gives the wisp an informational role: it helps the player notice architectural landmarks they might otherwise miss.

All three agents find the player each frame by tag (re-finding is necessary because `MapGenerator.SpawnPlayer` destroys and recreates the player GameObject on every regeneration). They use `MapGenerator.currentMap` and `GridToWorldPoint()` for navigation. The `AgentSpawner` script destroys all existing agents and instantiates a fresh set at random valid positions (at least 8 units away from the player) every time `GenerateMap` completes, guaranteeing clean behaviour across regenerations.

---

## Stretch Goal: Generator Evaluation (ERA)

`Assets/Scripts/Evaluation.cs` implements Expressive Range Analysis (Smith & Whitehead, 2010) comparing the modified generator against Lague's original.

**Method.** Lague's `RandomFillMap` is kept as a fallback and selected when `useBSP = false`. A public method `GenerateMapForEvaluation(seed, useBSP)` runs the full pipeline without spawning a player or a mesh, returning the raw grid. The evaluation script generates `sampleCount` maps per configuration using deterministic seeds (`eval_0`, `eval_1`, …). Using the same seeds across both generators ensures any difference in the output reflects the generator logic, not the seed stream.

Two metrics are computed per map:

- **Openness**: fraction of empty cells over total cells. A coarse measure of how much space is walkable.
- **Room count**: number of distinct connected empty regions ≥ 50 tiles (matching `MapGenerator`'s small-region threshold), via BFS flood fill. A measure of how fragmented the level is.

Results are written to `Assets/evaluation_results.csv` for external plotting. The two axes (openness × room count) produce a scatter cloud per generator representing that generator's expressive range.

**Results.** Running with 50 samples per generator:

| Generator | Openness (mean ± std) | Room count (mean ± std) |
|---|---|---|
| Lague (random fill) | 0.47 ± 0.04 | 2.1 ± 1.7 |
| Modified (BSP) | 0.42 ± 0.06 | 5.3 ± 1.4 |

Plotted as a scatter with openness on the X axis and room count on the Y axis, the two generators occupy distinctly different regions. Lague's cluster sits tightly at moderate openness and low room counts: most outputs are one or two large caverns. The modified cluster sits at slightly lower openness but much higher and more consistent room counts, typically 4 to 7 rooms.

**Conclusions.** The modified generator has a narrower openness range but a substantially higher and more consistent room count. This is appropriate for the concept: Library of the Magus requires levels that read as multiple distinct chambers connected by corridors, not one large cavern. BSP enforces the room-grid structure more reliably than random fill, even after cellular automata erosion.

Lague's generator has wider raw diversity on the room-count axis (anywhere from 1 to 12), but this diversity includes outputs (single-room caverns) that would be unsuitable for the concept. The modified generator trades raw diversity for *usable* diversity; every output contains multiple rooms, two portals, and architectural landmarks, matching the design brief.

A limitation of the evaluation: only two structural metrics are used. Richer analysis would include path-length distribution, symmetry, and landmark density. These were out of scope for the submission but would be natural extensions.

---

## Third-Party Code and GenAI Use

- **Base project**: Sebastian Lague, *Procedural Cave Generation* ([YouTube playlist](https://www.youtube.com/playlist?list=PLFt_AvWsXl0eZgMK_DT5_biRkWXftAOf9), [GitHub](https://github.com/SebLague/Procedural-Cave-Generation/)). Used as the required starting point for the assignment. All unchanged methods remain attributed to Lague.
- **GenAI**: Used as a collaborator during design and implementation, brainstorming Stage 1 alternatives (evaluated Perlin noise, drunkard's walk, and BSP before committing to BSP), drafting code comments, debugging edge cases, and editing this README. All design decisions, algorithm choices, and final code were reviewed, tested, and critically evaluated by me before submission. I can explain and defend every line.