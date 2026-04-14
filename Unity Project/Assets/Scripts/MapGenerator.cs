using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;

/// <summary>
/// Procedural level generator based on cellular automata.
/// Original code by Sebastian Lague (Procedural Cave Generation tutorial).
/// Modified for "Verdant Labyrinth" — a forest-themed exploration game.
/// 
/// Modifications:
///   Stage 1: BSP (Binary Space Partition) cell initialisation (replaces pure random fill).
///   Stage 3: Enhanced post-processing with clearings, exit rooms,
///            landmarks, and variable-width passages.
/// </summary>
public class MapGenerator : MonoBehaviour
{
	// =====================================================================
	// FIELDS
	// =====================================================================

	/// Width of the generated map grid in cells.
	public int width;

	/// Height of the generated map grid in cells.
	public int height;

	/// Prefab instantiated as the player character in the level.
	public GameObject playerPrefab;

	/// Seed string used for deterministic pseudo-random generation.
	public string seed;

	/// When true, a new seed is generated from the current time each run.
	public bool useRandomSeed;

	/// Probability (0-100) that a cell starts as a wall in the original random fill.
	[Range(0, 100)]
	public int randomFillPercent;

	// ----- NEW CODE: Stage 1 — BSP (Binary Space Partition) Parameters -----
	// These parameters control the BSP algorithm used to initialise the map grid.
	// The map is recursively split into rectangular leaves, and a room is carved
	// inside each leaf, producing structured layouts with distinct rooms.

	[Header("NEW — Stage 1: BSP Initialisation")]

	/// When true, uses BSP initialisation; when false, falls back to original random fill.
	public bool useBSP = true;

	/// Minimum width or height a BSP leaf can have. Splitting stops when a leaf
	/// dimension falls below this value. Smaller values produce more, smaller rooms.
	/// Larger values produce fewer, bigger rooms.
	[Range(10, 40)]
	public int minLeafSize = 20;

	/// Number of cells of wall padding between the carved room and the leaf edge.
	/// Higher values create thicker walls and wider corridors between rooms.
	/// Lower values make rooms fill more of their leaf, leaving narrow gaps.
	[Range(1, 6)]
	public int roomPadding = 3;

	// ----- NEW CODE: Stage 3 — Post-Processing Parameters -----
	// These parameters control the enhanced post-processing: clearing creation,
	// entrance structures, landmark placement, and variable-width passage carving.

	[Header("NEW — Stage 3: Post-Processing")]

	/// Minimum number of tiles a room must contain before a clearing is carved at
	/// its centroid. Prevents tiny rooms from receiving unnecessary clearings.
	public int clearingMinRoomSize = 80;

	/// Radius of the circular clearing carved at the centroid of qualifying rooms.
	/// Larger values create more spacious open areas in the forest.
	[Range(2, 8)]
	public int clearingRadius = 4;

	/// Minimum radius of passages carved between connected rooms.
	[Range(1, 5)]
	public int minPassageWidth = 2;

	/// Maximum radius of passages carved between rooms connected to the main room.
	/// Wider passages create a clear hierarchy of major and minor paths.
	[Range(3, 10)]
	public int maxPassageWidth = 6;

	/// NEW CODE: half-size of the square exit room carved into the level.
	/// A value of 6 creates a 13x13 room (2*6+1). The exit is placed in the room
	/// furthest from the player's starting area.
	[Range(4, 10)]
	public int exitRoomHalfSize = 6;

	// ----- Private state -----

	/// Reference to the currently spawned player GameObject, destroyed on regeneration.
	private GameObject playerInstance;

	/// The 2D grid representing the level. 0 = empty/path, 1 = wall/thicket.
	int[,] map;

	/// NEW CODE: stores the grid positions of the two exit room centres so the
	/// player can be spawned inside one of them.
	private List<Coord> exitCentres = new List<Coord>();

	/// NEW CODE: cached list of surviving rooms from the most recent generation,
	/// used by agents and evaluation scripts to query level structure.
	public List<Room> currentRooms { get; private set; }

	/// NEW CODE: public accessor for the raw map grid, used by agents for pathfinding.
	public int[,] currentMap { get { return map; } }

	// =====================================================================
	// UNITY LIFECYCLE
	// =====================================================================

	/// Called once when the game starts. Triggers initial map generation.
	void Start()
	{
		GenerateMap();
	}

	/// Called every frame. Regenerates the map when the left mouse button is clicked,
	/// allowing rapid iteration during development.
	void Update()
	{
		if (Input.GetMouseButtonDown(0))
		{
			GenerateMap();
		}
	}

	// =====================================================================
	// MAP GENERATION PIPELINE
	// =====================================================================

	/// <summary>
	/// Main generation pipeline. Orchestrates all stages of level creation:
	/// 1. Initialise the grid (Stage 1).
	/// 2. Apply cellular automata smoothing (Stage 2).
	/// 3. Post-process: remove small regions, connect rooms, carve clearings,
	///    place entrance structures, scatter landmarks (Stage 3).
	/// 4. Add a solid border around the map.
	/// 5. Spawn the player.
	/// 6. Generate the visual mesh.
	/// </summary>
	void GenerateMap()
	{
		map = new int[width, height];

		// ----- Stage 1: Initialisation -----
		// NEW CODE: choose between BSP and original random fill
		if (useBSP)
		{
			BSPFillMap();          // NEW CODE: BSP initialisation
		}
		else
		{
			RandomFillMap();       // Original random fill (kept as fallback for evaluation)
		}

		// ----- Stage 2: Cellular Automata Smoothing -----
		// Five iterations of the smoothing function refine the noisy grid into
		// coherent regions of wall and empty space.
		for (int i = 0; i < 5; i++)
		{
			SmoothMap();
		}

		// ----- Stage 3: Post-processing -----
		ProcessMap();

		// ----- Border -----
		// Adds a solid wall border around the entire map to prevent the player
		// from walking off the edge. The border is 1 cell wide.
		int borderSize = 1;
		int[,] borderedMap = new int[width + borderSize * 2, height + borderSize * 2];

		for (int x = 0; x < borderedMap.GetLength(0); x++)
		{
			for (int y = 0; y < borderedMap.GetLength(1); y++)
			{
				if (x >= borderSize && x < width + borderSize && y >= borderSize && y < height + borderSize)
				{
					borderedMap[x, y] = map[x - borderSize, y - borderSize];
				}
				else
				{
					borderedMap[x, y] = 1;
				}
			}
		}

		// ----- Player & Mesh -----
		SpawnPlayer();
		MeshGenerator meshGen = GetComponent<MeshGenerator>();
		meshGen.GenerateMesh(borderedMap, 1);
	}

	// =====================================================================
	// STAGE 1: CELL INITIALISATION
	// =====================================================================

	/// <summary>
	/// NEW CODE — Stage 1 replacement.
	/// Fills the map grid using Binary Space Partition (BSP). The map starts
	/// entirely solid (all walls). The full area is recursively split into
	/// smaller rectangular leaves by alternating vertical and horizontal cuts.
	/// Once a leaf is too small to split further, a room is carved inside it.
	///
	/// Technique reference: BSP is a classic dungeon generation technique widely
	/// used in roguelikes such as NetHack. See Shaker, Shaker and Togelius,
	/// "Procedural Content Generation in Games" (Springer, 2016), Chapter 3.
	///
	/// The result is a grid of distinct rectangular rooms separated by walls.
	/// The cellular automata smoothing in Stage 2 then softens the sharp
	/// rectangular edges into organic, natural-looking shapes while preserving
	/// the underlying room structure — producing levels that feel both
	/// structured and organic, fitting the overgrown-ruins concept.
	/// </summary>
	void BSPFillMap()
	{
		// Derive a deterministic seed
		if (useRandomSeed)
		{
			seed = Time.time.ToString();
		}

		System.Random prng = new System.Random(seed.GetHashCode());

		// NEW CODE: start with a fully solid grid (all walls).
		// BSP then carves rooms into this solid space.
		for (int x = 0; x < width; x++)
		{
			for (int y = 0; y < height; y++)
			{
				map[x, y] = 1;
			}
		}

		// --- BSP recursive splitting ---
		// Each leaf is stored as an int array: [startX, startY, leafWidth, leafHeight].
		// We split leaves until they are smaller than minLeafSize, then carve rooms.
		List<int[]> leaves = new List<int[]>();
		List<int[]> toProcess = new List<int[]>();

		// Start with the full map minus a 1-cell border on all sides
		toProcess.Add(new int[] { 1, 1, width - 2, height - 2 });

		while (toProcess.Count > 0)
		{
			int[] current = toProcess[0];
			toProcess.RemoveAt(0);

			int lx = current[0];  // leaf start X
			int ly = current[1];  // leaf start Y
			int lw = current[2];  // leaf width
			int lh = current[3];  // leaf height

			// Check if this leaf can be split in each direction
			bool canSplitH = lh >= minLeafSize * 2;
			bool canSplitV = lw >= minLeafSize * 2;

			if (!canSplitH && !canSplitV)
			{
				// Too small to split — this becomes a final leaf
				leaves.Add(current);
				continue;
			}

			// Choose split direction: prefer splitting the longer axis.
			// If both axes are long enough, split the longer one to keep
			// rooms roughly square. Adds randomness when dimensions are similar.
			bool splitHorizontal;
			if (canSplitH && canSplitV)
			{
				splitHorizontal = (lh > lw) || (lh == lw && prng.Next(0, 2) == 0);
			}
			else
			{
				splitHorizontal = canSplitH;
			}

			if (splitHorizontal)
			{
				// Split horizontally: choose a Y position within the valid range
				int splitPos = ly + prng.Next(minLeafSize, lh - minLeafSize + 1);
				toProcess.Add(new int[] { lx, ly, lw, splitPos - ly });
				toProcess.Add(new int[] { lx, splitPos, lw, ly + lh - splitPos });
			}
			else
			{
				// Split vertically: choose an X position within the valid range
				int splitPos = lx + prng.Next(minLeafSize, lw - minLeafSize + 1);
				toProcess.Add(new int[] { lx, ly, splitPos - lx, lh });
				toProcess.Add(new int[] { splitPos, ly, lx + lw - splitPos, lh });
			}
		}

		// --- Carve a room inside each final leaf ---
		// Each room is the leaf area shrunk inward by roomPadding on all sides.
		// The padding becomes the walls/corridors between rooms.
		foreach (int[] leaf in leaves)
		{
			int lx = leaf[0];
			int ly = leaf[1];
			int lw = leaf[2];
			int lh = leaf[3];

			// Calculate room bounds within the leaf
			int roomX = lx + roomPadding;
			int roomY = ly + roomPadding;
			int roomW = lw - roomPadding * 2;
			int roomH = lh - roomPadding * 2;

			// Skip if the room would be too small after padding
			if (roomW < 3 || roomH < 3)
			{
				continue;
			}

			// Carve the room (set all cells to empty)
			for (int x = roomX; x < roomX + roomW; x++)
			{
				for (int y = roomY; y < roomY + roomH; y++)
				{
					if (IsInMapRange(x, y))
					{
						map[x, y] = 0;
					}
				}
			}
		}
	}

	/// <summary>
	/// Original Stage 1 initialisation by Sebastian Lague.
	/// Fills each cell randomly based on randomFillPercent, with border cells
	/// always set to walls. Kept as a fallback for comparison and evaluation.
	/// </summary>
	void RandomFillMap()
	{
		if (useRandomSeed)
		{
			seed = Time.time.ToString();
		}

		System.Random pseudoRandom = new System.Random(seed.GetHashCode());

		for (int x = 0; x < width; x++)
		{
			for (int y = 0; y < height; y++)
			{
				// Border cells are always walls
				if (x == 0 || x == width - 1 || y == 0 || y == height - 1)
				{
					map[x, y] = 1;
				}
				else
				{
					map[x, y] = (pseudoRandom.Next(0, 100) < randomFillPercent) ? 1 : 0;
				}
			}
		}
	}

	// =====================================================================
	// STAGE 2: CELLULAR AUTOMATA SMOOTHING
	// =====================================================================

	/// <summary>
	/// Applies one iteration of cellular automata smoothing (4-5 rule).
	/// Each cell counts its 8 neighbours: if more than 4 are walls, it becomes a wall;
	/// if fewer than 4 are walls, it becomes empty. Cells with exactly 4 wall neighbours
	/// remain unchanged. This produces smooth, organic shapes from noisy input.
	/// </summary>
	void SmoothMap()
	{
		for (int x = 0; x < width; x++)
		{
			for (int y = 0; y < height; y++)
			{
				int neighbourWallTiles = GetSurroundingWallCount(x, y);

				if (neighbourWallTiles > 4)
					map[x, y] = 1;
				else if (neighbourWallTiles < 4)
					map[x, y] = 0;
			}
		}
	}

	/// <summary>
	/// Counts the number of wall cells in the 3x3 Moore neighbourhood of (gridX, gridY),
	/// excluding the cell itself. Out-of-bounds neighbours are counted as walls, which
	/// encourages walls along map edges.
	/// </summary>
	int GetSurroundingWallCount(int gridX, int gridY)
	{
		int wallCount = 0;
		for (int neighbourX = gridX - 1; neighbourX <= gridX + 1; neighbourX++)
		{
			for (int neighbourY = gridY - 1; neighbourY <= gridY + 1; neighbourY++)
			{
				if (IsInMapRange(neighbourX, neighbourY))
				{
					if (neighbourX != gridX || neighbourY != gridY)
					{
						wallCount += map[neighbourX, neighbourY];
					}
				}
				else
				{
					// Out-of-bounds cells count as walls
					wallCount++;
				}
			}
		}
		return wallCount;
	}

	// =====================================================================
	// STAGE 3: POST-PROCESSING
	// =====================================================================

	/// <summary>
	/// Main post-processing pipeline.
	/// Original behaviour: removes small wall/room regions, then connects all
	/// surviving rooms via passages.
	/// 
	/// NEW CODE additions:
	///   - CreateClearings(): carves circular open areas in large rooms.
	///   - CreateExitRoom(): carves two square exit chambers in the furthest rooms.
	///   - PlaceLandmarks(): adds small wall clusters inside large clearings.
	///   - SmoothPassageEdges(): softens corridor boundaries for a natural look.
	/// </summary>
	void ProcessMap()
	{
		// --- Original: remove small wall regions ---
		// Wall clusters smaller than the threshold are removed (set to empty),
		// eliminating tiny isolated pillars that clutter the level.
		List<List<Coord>> wallRegions = GetRegions(1);
		int wallThresholdSize = 50;

		foreach (List<Coord> wallRegion in wallRegions)
		{
			if (wallRegion.Count < wallThresholdSize)
			{
				foreach (Coord tile in wallRegion)
				{
					map[tile.tileX, tile.tileY] = 0;
				}
			}
		}

		// --- Original: remove small room regions and collect surviving rooms ---
		// Rooms smaller than the threshold are filled in (set to wall), removing
		// tiny pockets that would be inaccessible or uninteresting.
		List<List<Coord>> roomRegions = GetRegions(0);
		int roomThresholdSize = 50;
		List<Room> survivingRooms = new List<Room>();

		foreach (List<Coord> roomRegion in roomRegions)
		{
			if (roomRegion.Count < roomThresholdSize)
			{
				foreach (Coord tile in roomRegion)
				{
					map[tile.tileX, tile.tileY] = 1;
				}
			}
			else
			{
				survivingRooms.Add(new Room(roomRegion, map));
			}
		}

		// Sort rooms by size (largest first) so the main room is index 0
		survivingRooms.Sort();
		survivingRooms[0].isMainRoom = true;
		survivingRooms[0].isAccessibleFromMainRoom = true;

		// --- Original: connect rooms via passages ---
		ConnectClosestRooms(survivingRooms);

		// --- NEW CODE: Stage 3 enhancements ---

		// NEW CODE: apply smoothing first so it can't erase exit walls, columns, or clearings.
		SmoothPassageEdges();

		// NEW CODE: carve exit rooms after smoothing so their walls survive.
		CreateExitRoom(survivingRooms);

		// NEW CODE: carve circular clearings after exits so we can skip rooms near exits.
		CreateClearings(survivingRooms);

		// NEW CODE: place column landmarks last, excluding exit areas.
		PlaceLandmarks(survivingRooms);

		// NEW CODE: store the surviving rooms for use by agents and evaluation
		currentRooms = survivingRooms;
	}

	/// <summary>
	/// NEW CODE — Stage 3 enhancement.
	/// For each room exceeding clearingMinRoomSize, calculates the centroid of
	/// the room tiles and carves a circular clearing of radius clearingRadius.
	/// This transforms irregular cellular automata regions into recognisable
	/// open clearings, reinforcing the forest-clearing theme.
	///
	/// The main room receives a larger clearing to serve as a natural starting
	/// area and landmark for navigation.
	/// </summary>
	void CreateClearings(List<Room> rooms)
	{
		foreach (Room room in rooms)
		{
			if (room.roomSize < clearingMinRoomSize)
			{
				continue;
			}

			// Calculate the centroid (average position) of all tiles in the room
			float centreX = 0;
			float centreY = 0;
			foreach (Coord tile in room.tiles)
			{
				centreX += tile.tileX;
				centreY += tile.tileY;
			}
			centreX /= room.tiles.Count;
			centreY /= room.tiles.Count;

			// Skip this room if its centroid falls inside or near an exit room,
			// so clearings don't carve into exit walls
			int cx = Mathf.RoundToInt(centreX);
			int cy = Mathf.RoundToInt(centreY);
			bool nearExit = false;
			foreach (Coord ec in exitCentres)
			{
				if (cx >= ec.tileX - exitRoomHalfSize - 2 && cx <= ec.tileX + exitRoomHalfSize + 2 &&
					cy >= ec.tileY - exitRoomHalfSize - 2 && cy <= ec.tileY + exitRoomHalfSize + 2)
				{
					nearExit = true;
					break;
				}
			}
			if (nearExit)
			{
				continue;
			}

			// Determine clearing radius: larger rooms get slightly bigger clearings
			int radius = clearingRadius;
			if (room.roomSize > clearingMinRoomSize * 2)
			{
				radius = clearingRadius + 1;
			}
			if (room.isMainRoom)
			{
				radius = clearingRadius + 2; // Main room gets the largest clearing
			}

			// Carve a circular clearing centred on the room's centroid
			Coord centre = new Coord(Mathf.RoundToInt(centreX), Mathf.RoundToInt(centreY));
			DrawCircle(centre, radius);
		}
	}

	/// <summary>
	/// NEW CODE — Stage 3 enhancement.
	/// Creates exactly two square exit rooms. First tries to place them in the
	/// two rooms furthest from the main room. If fewer than two candidate rooms
	/// exist, uses fixed fallback positions (bottom-left and top-right quadrants)
	/// so that two exits are always guaranteed regardless of level layout.
	///
	/// Exit centres are stored in exitCentres so SpawnPlayer can place the
	/// player inside one of them.
	/// </summary>
	void CreateExitRoom(List<Room> rooms)
	{
		exitCentres.Clear();

		int s = exitRoomHalfSize;
		int margin = s + 3;

		// --- Collect candidate positions from room centroids ---
		float mainCx = width / 2f;
		float mainCy = height / 2f;

		if (rooms != null && rooms.Count > 0)
		{
			Room mainRoom = rooms.Find(r => r.isMainRoom);
			if (mainRoom == null) mainRoom = rooms[0];

			mainCx = 0; mainCy = 0;
			foreach (Coord tile in mainRoom.tiles)
			{
				mainCx += tile.tileX;
				mainCy += tile.tileY;
			}
			mainCx /= mainRoom.tiles.Count;
			mainCy /= mainRoom.tiles.Count;
		}

		// Rank non-main rooms by distance from main room
		List<Room> candidates = new List<Room>();
		List<float> dists = new List<float>();

		if (rooms != null)
		{
			foreach (Room room in rooms)
			{
				if (room.isMainRoom) continue;
				float cx = 0, cy = 0;
				foreach (Coord tile in room.tiles)
				{
					cx += tile.tileX;
					cy += tile.tileY;
				}
				cx /= room.tiles.Count;
				cy /= room.tiles.Count;
				float dist = (cx - mainCx) * (cx - mainCx) + (cy - mainCy) * (cy - mainCy);
				candidates.Add(room);
				dists.Add(dist);
			}
		}

		// Find the furthest room for exit 1
		int firstIdx = -1; float firstDist = -1;
		for (int i = 0; i < candidates.Count; i++)
		{
			if (dists[i] > firstDist) { firstDist = dists[i]; firstIdx = i; }
		}

		// Calculate centroid of exit 1 (needed to enforce distance from exit 2)
		float firstCx = 0, firstCy = 0;
		if (firstIdx >= 0)
		{
			foreach (Coord tile in candidates[firstIdx].tiles)
			{
				firstCx += tile.tileX;
				firstCy += tile.tileY;
			}
			firstCx /= candidates[firstIdx].tiles.Count;
			firstCy /= candidates[firstIdx].tiles.Count;
		}

		// Find exit 2: must be far from BOTH the main room AND exit 1.
		// Minimum distance between the two exits is 1/3 of the map diagonal.
		float minExitSeparation = (width * width + height * height) / 9f; // squared distance threshold
		int secondIdx = -1; float secondDist = -1;
		for (int i = 0; i < candidates.Count; i++)
		{
			if (i == firstIdx) continue;

			// Check distance from exit 1
			float cx = 0, cy = 0;
			foreach (Coord tile in candidates[i].tiles)
			{
				cx += tile.tileX;
				cy += tile.tileY;
			}
			cx /= candidates[i].tiles.Count;
			cy /= candidates[i].tiles.Count;

			float distFromFirst = (cx - firstCx) * (cx - firstCx) + (cy - firstCy) * (cy - firstCy);

			// Only consider this room if it's far enough from exit 1
			if (distFromFirst >= minExitSeparation && dists[i] > secondDist)
			{
				secondDist = dists[i];
				secondIdx = i;
			}
		}

		// --- Place exit 1 ---
		if (firstIdx >= 0)
		{
			BuildSquareExit(candidates[firstIdx], s, margin);
		}
		else
		{
			// Fallback: fixed position in bottom-left quadrant
			BuildSquareExitAt(margin, margin, s);
		}

		// --- Place exit 2 ---
		if (secondIdx >= 0)
		{
			BuildSquareExit(candidates[secondIdx], s, margin);
		}
		else
		{
			// Fallback: opposite corner from exit 1, guaranteeing separation
			if (firstCx < width / 2f)
				BuildSquareExitAt(width - margin - 1, height - margin - 1, s);
			else
				BuildSquareExitAt(margin, margin, s);
		}
	}

	/// <summary>
	/// NEW CODE — helper for CreateExitRoom.
	/// Builds a square exit at a room's centroid, clamped to stay within the map.
	/// Stores the centre position in exitCentres for player spawning.
	/// </summary>
	void BuildSquareExit(Room room, int s, int margin)
	{
		float centreX = 0, centreY = 0;
		foreach (Coord tile in room.tiles)
		{
			centreX += tile.tileX;
			centreY += tile.tileY;
		}
		centreX /= room.tiles.Count;
		centreY /= room.tiles.Count;

		int cx = Mathf.Clamp(Mathf.RoundToInt(centreX), margin, width - margin - 1);
		int cy = Mathf.Clamp(Mathf.RoundToInt(centreY), margin, height - margin - 1);

		BuildSquareExitAt(cx, cy, s);
	}

	/// <summary>
	/// NEW CODE — helper for CreateExitRoom.
	/// Builds a single square exit chamber at exact grid position (cx, cy).
	///
	/// Steps:
	///   1. Clear the square interior plus a 1-cell margin around the centroid.
	///   2. Build a solid wall perimeter forming a sharp-edged rectangle.
	///   3. Punch wide doorways on each of the four sides.
	///   4. Clear the four corner cells so no stray wall blocks remain.
	///   5. Store the centre in exitCentres so the player can spawn there.
	///
	/// Connectivity is handled by CreateExitRoom which draws a passage from
	/// each exit to the map centre after all exits are built.
	/// </summary>
	void BuildSquareExitAt(int cx, int cy, int s)
	{
		// Clamp to guarantee it fits
		cx = Mathf.Clamp(cx, s + 2, width - s - 3);
		cy = Mathf.Clamp(cy, s + 2, height - s - 3);

		// --- Step 1: Clear the square interior plus a 1-cell margin ---
		for (int x = cx - s - 1; x <= cx + s + 1; x++)
		{
			for (int y = cy - s - 1; y <= cy + s + 1; y++)
			{
				if (IsInMapRange(x, y))
				{
					map[x, y] = 0;
				}
			}
		}

		// --- Step 2: Build the square wall perimeter ---
		for (int x = cx - s; x <= cx + s; x++)
		{
			for (int y = cy - s; y <= cy + s; y++)
			{
				bool isPerimeter = (x == cx - s || x == cx + s || y == cy - s || y == cy + s);
				if (isPerimeter && IsInMapRange(x, y))
				{
					map[x, y] = 1;
				}
			}
		}

		// --- Step 3: Punch wide doorways on all four sides ---
		// halfGap = 3 creates a 7-cell opening, wide enough for the player to pass through
		int halfGap = 3;

		// South wall
		for (int x = cx - halfGap; x <= cx + halfGap; x++)
			if (IsInMapRange(x, cy - s)) map[x, cy - s] = 0;

		// North wall
		for (int x = cx - halfGap; x <= cx + halfGap; x++)
			if (IsInMapRange(x, cy + s)) map[x, cy + s] = 0;

		// West wall
		for (int y = cy - halfGap; y <= cy + halfGap; y++)
			if (IsInMapRange(cx - s, y)) map[cx - s, y] = 0;

		// East wall
		for (int y = cy - halfGap; y <= cy + halfGap; y++)
			if (IsInMapRange(cx + s, y)) map[cx + s, y] = 0;

		// --- Step 4: Clear the four corners so no stray wall blocks remain ---
		if (IsInMapRange(cx - s, cy - s)) map[cx - s, cy - s] = 0; // bottom-left
		if (IsInMapRange(cx + s, cy - s)) map[cx + s, cy - s] = 0; // bottom-right
		if (IsInMapRange(cx - s, cy + s)) map[cx - s, cy + s] = 0; // top-left
		if (IsInMapRange(cx + s, cy + s)) map[cx + s, cy + s] = 0; // top-right

		// --- Step 5: Store the exit centre for player spawning ---
		exitCentres.Add(new Coord(cx, cy));
	}

	/// <summary>
	/// NEW CODE — Stage 3 enhancement.
	/// Places small wall clusters (1-2 cell pillars) inside large rooms at random
	/// positions, representing ancient trees, stone pillars, or ruins. These
	/// landmarks break up large empty spaces, adding visual interest and
	/// tactical cover for gameplay.
	///
	/// Landmarks are only placed in cells surrounded by enough empty space to
	/// avoid blocking passages or doorways.
	/// </summary>
	void PlaceLandmarks(List<Room> rooms)
	{
		System.Random prng = new System.Random(seed.GetHashCode() + 42);

		foreach (Room room in rooms)
		{
			// Skip small rooms — not enough space for a column
			if (room.roomSize < clearingMinRoomSize)
			{
				continue;
			}

			// Random chance to skip this room entirely (roughly half the rooms get a column)
			if (prng.Next(0, 100) >= 50)
			{
				continue;
			}

			// Try to find a valid position for the column inside this room.
			// Shuffle through room tiles randomly and pick the first one
			// that has a fully clear 7x7 area around it.
			List<Coord> shuffled = new List<Coord>(room.tiles);
			for (int i = shuffled.Count - 1; i > 0; i--)
			{
				int j = prng.Next(0, i + 1);
				Coord temp = shuffled[i];
				shuffled[i] = shuffled[j];
				shuffled[j] = temp;
			}

			bool placed = false;
			foreach (Coord tile in shuffled)
			{
				int tx = tile.tileX;
				int ty = tile.tileY;

				// Skip tiles inside or near any exit room
				bool insideExit = false;
				foreach (Coord ec in exitCentres)
				{
					if (tx >= ec.tileX - exitRoomHalfSize - 1 && tx <= ec.tileX + exitRoomHalfSize + 1 &&
						ty >= ec.tileY - exitRoomHalfSize - 1 && ty <= ec.tileY + exitRoomHalfSize + 1)
					{
						insideExit = true;
						break;
					}
				}
				if (insideExit)
				{
					continue;
				}

				// Need a 7x7 clear area (3x3 column + 2 margin each side)
				if (tx < 4 || tx >= width - 4 || ty < 4 || ty >= height - 4)
				{
					continue;
				}

				// Check the 7x7 area is fully empty
				bool clear = true;
				for (int nx = tx - 3; nx <= tx + 3 && clear; nx++)
				{
					for (int ny = ty - 3; ny <= ty + 3 && clear; ny++)
					{
						if (map[nx, ny] != 0)
						{
							clear = false;
						}
					}
				}

				if (!clear)
				{
					continue;
				}

				// Place a 3x3 wall block — looks like a ruined column or pillar
				for (int bx = tx - 1; bx <= tx + 1; bx++)
				{
					for (int by = ty - 1; by <= ty + 1; by++)
					{
						map[bx, by] = 1;
					}
				}

				placed = true;
				break; // Only one column per room
			}
		}
	}

	/// <summary>
	/// NEW CODE — Stage 3 enhancement.
	/// Applies a single pass of conditional smoothing that only converts isolated
	/// wall cells (those with few wall neighbours) into empty space. This softens
	/// the edges of passages carved by CreatePassage without significantly
	/// altering room shapes, producing more natural-looking winding paths.
	///
	/// A clone of the map is read from to avoid cascading changes within a single pass.
	/// </summary>
	void SmoothPassageEdges()
	{
		int[,] mapCopy = (int[,])map.Clone();

		for (int x = 1; x < width - 1; x++)
		{
			for (int y = 1; y < height - 1; y++)
			{
				int wallNeighbours = GetSurroundingWallCount(x, y);

				// Only remove wall cells mostly surrounded by empty space
				// (small protrusions into corridors). Preserves overall room
				// structure while smoothing passage edges.
				if (mapCopy[x, y] == 1 && wallNeighbours < 3)
				{
					map[x, y] = 0;
				}
			}
		}
	}

	// =====================================================================
	// ROOM CONNECTION (Original code by Lague, with NEW CODE modifications)
	// =====================================================================

	/// <summary>
	/// Connects each room to its nearest unconnected room by carving a passage.
	/// When forceAccessibilityFromMainRoom is true, ensures every room is
	/// reachable from the main room (the largest room).
	/// 
	/// Algorithm: for each pair of rooms, compares squared distances between all
	/// edge tiles to find the closest pair, then carves a passage between them.
	/// </summary>
	void ConnectClosestRooms(List<Room> allRooms, bool forceAccessibilityFromMainRoom = false)
	{
		// Separate rooms into two lists:
		// roomListA = rooms not yet accessible from main room
		// roomListB = rooms already accessible from main room
		List<Room> roomListA = new List<Room>();
		List<Room> roomListB = new List<Room>();

		if (forceAccessibilityFromMainRoom)
		{
			foreach (Room room in allRooms)
			{
				if (room.isAccessibleFromMainRoom)
				{
					roomListB.Add(room);
				}
				else
				{
					roomListA.Add(room);
				}
			}
		}
		else
		{
			roomListA = allRooms;
			roomListB = allRooms;
		}

		int bestDistance = 0;
		Coord bestTileA = new Coord();
		Coord bestTileB = new Coord();
		Room bestRoomA = new Room();
		Room bestRoomB = new Room();
		bool possibleConnectionFound = false;

		foreach (Room roomA in roomListA)
		{
			if (!forceAccessibilityFromMainRoom)
			{
				possibleConnectionFound = false;
				if (roomA.connectedRooms.Count > 0)
				{
					continue;
				}
			}

			foreach (Room roomB in roomListB)
			{
				if (roomA == roomB || roomA.IsConnected(roomB))
				{
					continue;
				}

				// Compare every edge tile pair to find the minimum distance
				for (int tileIndexA = 0; tileIndexA < roomA.edgeTiles.Count; tileIndexA++)
				{
					for (int tileIndexB = 0; tileIndexB < roomB.edgeTiles.Count; tileIndexB++)
					{
						Coord tileA = roomA.edgeTiles[tileIndexA];
						Coord tileB = roomB.edgeTiles[tileIndexB];
						int distanceBetweenRooms = (int)(Mathf.Pow(tileA.tileX - tileB.tileX, 2) + Mathf.Pow(tileA.tileY - tileB.tileY, 2));

						if (distanceBetweenRooms < bestDistance || !possibleConnectionFound)
						{
							bestDistance = distanceBetweenRooms;
							possibleConnectionFound = true;
							bestTileA = tileA;
							bestTileB = tileB;
							bestRoomA = roomA;
							bestRoomB = roomB;
						}
					}
				}
			}
			if (possibleConnectionFound && !forceAccessibilityFromMainRoom)
			{
				CreatePassage(bestRoomA, bestRoomB, bestTileA, bestTileB);
			}
		}

		if (possibleConnectionFound && forceAccessibilityFromMainRoom)
		{
			CreatePassage(bestRoomA, bestRoomB, bestTileA, bestTileB);
			ConnectClosestRooms(allRooms, true);
		}

		if (!forceAccessibilityFromMainRoom)
		{
			ConnectClosestRooms(allRooms, true);
		}
	}

	/// <summary>
	/// Carves a passage between two rooms by drawing circles along a line
	/// between two edge tiles.
	/// 
	/// NEW CODE: passage width now varies based on whether the connection
	/// involves the main room. Main-room connections use maxPassageWidth,
	/// creating wide "main paths"; other connections use minPassageWidth,
	/// creating narrower "side paths". This produces a navigable hierarchy
	/// of major and minor forest paths.
	/// </summary>
	void CreatePassage(Room roomA, Room roomB, Coord tileA, Coord tileB)
	{
		Room.ConnectRooms(roomA, roomB);

		// NEW CODE: variable passage width based on room importance
		int passageRadius;
		if (roomA.isMainRoom || roomB.isMainRoom)
		{
			passageRadius = maxPassageWidth;
		}
		else if (roomA.isAccessibleFromMainRoom && roomB.isAccessibleFromMainRoom)
		{
			passageRadius = (minPassageWidth + maxPassageWidth) / 2;
		}
		else
		{
			passageRadius = minPassageWidth;
		}

		List<Coord> line = GetLine(tileA, tileB);
		foreach (Coord c in line)
		{
			DrawCircle(c, passageRadius);
		}
	}

	// =====================================================================
	// PLAYER SPAWNING
	// =====================================================================

	/// <summary>
	/// Spawns the player inside one of the exit rooms. The first exit centre
	/// stored during CreateExitRoom is used as the spawn position, placing
	/// the player directly at the centre of an exit chamber.
	/// Falls back to any empty tile if no exit centres exist.
	/// </summary>
	void SpawnPlayer()
	{
		if (playerInstance != null)
		{
			Destroy(playerInstance);
		}

		// NEW CODE: spawn the player at the centre of the first exit room
		Vector3 spawnPos;

		if (exitCentres.Count > 0)
		{
			Coord spawn = exitCentres[0];
			spawnPos = CoordToWorldPoint(spawn);
		}
		else
		{
			// Fallback: find any empty tile
			List<Coord> emptyTiles = new List<Coord>();
			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					if (map[x, y] == 0)
					{
						emptyTiles.Add(new Coord(x, y));
					}
				}
			}
			Coord spawn = emptyTiles[UnityEngine.Random.Range(0, emptyTiles.Count)];
			spawnPos = CoordToWorldPoint(spawn);
		}

		playerInstance = Instantiate(playerPrefab, spawnPos, Quaternion.identity);
	}

	// =====================================================================
	// GEOMETRY UTILITIES
	// =====================================================================

	/// <summary>
	/// Sets all cells within a circle of radius r centred on coordinate c to empty (0).
	/// Used to carve passages and clearings.
	/// </summary>
	void DrawCircle(Coord c, int r)
	{
		for (int x = -r; x <= r; x++)
		{
			for (int y = -r; y <= r; y++)
			{
				if (x * x + y * y <= r * r)
				{
					int drawX = c.tileX + x;
					int drawY = c.tileY + y;
					if (IsInMapRange(drawX, drawY))
					{
						map[drawX, drawY] = 0;
					}
				}
			}
		}
	}

	/// <summary>
	/// Returns a list of grid coordinates forming a line from 'from' to 'to',
	/// using Bresenham's line algorithm. Used to determine which cells a
	/// passage should be carved through.
	/// </summary>
	List<Coord> GetLine(Coord from, Coord to)
	{
		List<Coord> line = new List<Coord>();

		int x = from.tileX;
		int y = from.tileY;

		int dx = to.tileX - from.tileX;
		int dy = to.tileY - from.tileY;

		bool inverted = false;
		int step = Math.Sign(dx);
		int gradientStep = Math.Sign(dy);

		int longest = Mathf.Abs(dx);
		int shortest = Mathf.Abs(dy);

		if (longest < shortest)
		{
			inverted = true;
			longest = Mathf.Abs(dy);
			shortest = Mathf.Abs(dx);

			step = Math.Sign(dy);
			gradientStep = Math.Sign(dx);
		}

		int gradientAccumulation = longest / 2;
		for (int i = 0; i < longest; i++)
		{
			line.Add(new Coord(x, y));

			if (inverted)
			{
				y += step;
			}
			else
			{
				x += step;
			}

			gradientAccumulation += shortest;
			if (gradientAccumulation >= longest)
			{
				if (inverted)
				{
					x += gradientStep;
				}
				else
				{
					y += gradientStep;
				}
				gradientAccumulation -= longest;
			}
		}

		return line;
	}

	/// <summary>
	/// Converts a grid coordinate to a world-space position. The map is centred
	/// at the origin, so (0,0) in grid space maps to (-width/2, 2, -height/2) in
	/// world space. The y=2 places the floor plane above the wall geometry.
	/// </summary>
	Vector3 CoordToWorldPoint(Coord tile)
	{
		return new Vector3(-width / 2 + .5f + tile.tileX, 2, -height / 2 + .5f + tile.tileY);
	}

	/// <summary>
	/// NEW CODE: public version of CoordToWorldPoint for use by agent scripts.
	/// Converts grid (x, y) to a world-space Vector3.
	/// </summary>
	public Vector3 GridToWorldPoint(int x, int y)
	{
		return new Vector3(-width / 2 + .5f + x, 2, -height / 2 + .5f + y);
	}

	/// <summary>
	/// Returns true if the coordinate (x, y) is within the map bounds.
	/// </summary>
	bool IsInMapRange(int x, int y)
	{
		return x >= 0 && x < width && y >= 0 && y < height;
	}

	// =====================================================================
	// REGION DETECTION (flood fill)
	// =====================================================================

	/// <summary>
	/// Returns all distinct contiguous regions of the specified tile type.
	/// A region is a connected group of cells sharing the same type (0 or 1),
	/// found using flood fill. Used to identify rooms (type 0) and wall clusters (type 1).
	/// </summary>
	List<List<Coord>> GetRegions(int tileType)
	{
		List<List<Coord>> regions = new List<List<Coord>>();
		int[,] mapFlags = new int[width, height];

		for (int x = 0; x < width; x++)
		{
			for (int y = 0; y < height; y++)
			{
				if (mapFlags[x, y] == 0 && map[x, y] == tileType)
				{
					List<Coord> newRegion = GetRegionTiles(x, y);
					regions.Add(newRegion);

					foreach (Coord tile in newRegion)
					{
						mapFlags[tile.tileX, tile.tileY] = 1;
					}
				}
			}
		}

		return regions;
	}

	/// <summary>
	/// Performs a BFS (breadth-first search) flood fill starting from (startX, startY),
	/// returning all contiguous cells of the same type. Only considers 4-directional
	/// (Von Neumann) neighbours to avoid diagonal leaking through wall corners.
	/// </summary>
	List<Coord> GetRegionTiles(int startX, int startY)
	{
		List<Coord> tiles = new List<Coord>();
		int[,] mapFlags = new int[width, height];
		int tileType = map[startX, startY];

		Queue<Coord> queue = new Queue<Coord>();
		queue.Enqueue(new Coord(startX, startY));
		mapFlags[startX, startY] = 1;

		while (queue.Count > 0)
		{
			Coord tile = queue.Dequeue();
			tiles.Add(tile);

			for (int x = tile.tileX - 1; x <= tile.tileX + 1; x++)
			{
				for (int y = tile.tileY - 1; y <= tile.tileY + 1; y++)
				{
					// Only 4-directional neighbours (not diagonals)
					if (IsInMapRange(x, y) && (y == tile.tileY || x == tile.tileX))
					{
						if (mapFlags[x, y] == 0 && map[x, y] == tileType)
						{
							mapFlags[x, y] = 1;
							queue.Enqueue(new Coord(x, y));
						}
					}
				}
			}
		}
		return tiles;
	}

	// =====================================================================
	// NEW CODE: EVALUATION HELPERS
	// =====================================================================

	/// <summary>
	/// NEW CODE: Generates a map without spawning a player or creating a mesh,
	/// returning the raw grid. Used by the Evaluation script to batch-generate
	/// levels and measure metrics without visual overhead.
	/// </summary>
	public int[,] GenerateMapForEvaluation(string evalSeed, bool useBspInit)
	{
		seed = evalSeed;
		useRandomSeed = false;
		map = new int[width, height];

		bool originalSetting = useBSP;
		useBSP = useBspInit;

		if (useBSP)
		{
			BSPFillMap();
		}
		else
		{
			RandomFillMap();
		}

		for (int i = 0; i < 5; i++)
		{
			SmoothMap();
		}

		ProcessMap();

		useBSP = originalSetting;
		return (int[,])map.Clone();
	}

	// =====================================================================
	// DATA STRUCTURES
	// =====================================================================

	/// <summary>
	/// Represents a 2D grid coordinate (tile position) within the map.
	/// </summary>
	public struct Coord
	{
		/// X position in the grid (column index).
		public int tileX;
		/// Y position in the grid (row index).
		public int tileY;

		public Coord(int x, int y)
		{
			tileX = x;
			tileY = y;
		}
	}

	/// <summary>
	/// Represents a contiguous region of empty tiles (a "room") in the generated level.
	/// Tracks the room's tiles, edge tiles, size, connections to other rooms, and
	/// whether it is reachable from the main room. Implements IComparable to allow
	/// sorting rooms by size (largest first).
	/// </summary>
	public class Room : IComparable<Room>
	{
		/// All tiles belonging to this room.
		public List<Coord> tiles;

		/// Tiles on the room's boundary (adjacent to at least one wall).
		public List<Coord> edgeTiles;

		/// Other rooms this room is directly connected to via carved passages.
		public List<Room> connectedRooms;

		/// Total number of tiles in the room.
		public int roomSize;

		/// True if this room can be reached from the main room (directly or indirectly).
		public bool isAccessibleFromMainRoom;

		/// True if this is the largest room in the level (designated as the main room).
		public bool isMainRoom;

		/// Default constructor for temporary/placeholder Room instances.
		public Room()
		{
		}

		/// <summary>
		/// Constructs a Room from a list of tiles, identifying which tiles lie on the
		/// room's edge by checking whether any 4-directional neighbour is a wall.
		/// </summary>
		public Room(List<Coord> roomTiles, int[,] map)
		{
			tiles = roomTiles;
			roomSize = tiles.Count;
			connectedRooms = new List<Room>();

			edgeTiles = new List<Coord>();
			foreach (Coord tile in tiles)
			{
				for (int x = tile.tileX - 1; x <= tile.tileX + 1; x++)
				{
					for (int y = tile.tileY - 1; y <= tile.tileY + 1; y++)
					{
						if (x == tile.tileX || y == tile.tileY)
						{
							if (map[x, y] == 1)
							{
								edgeTiles.Add(tile);
							}
						}
					}
				}
			}
		}

		/// <summary>
		/// Recursively marks this room and all rooms connected to it as accessible
		/// from the main room. Propagates through the connection graph.
		/// </summary>
		public void SetAccessibleFromMainRoom()
		{
			if (!isAccessibleFromMainRoom)
			{
				isAccessibleFromMainRoom = true;
				foreach (Room connectedRoom in connectedRooms)
				{
					connectedRoom.SetAccessibleFromMainRoom();
				}
			}
		}

		/// <summary>
		/// Establishes a bidirectional connection between two rooms and propagates
		/// main-room accessibility if either room is already accessible.
		/// </summary>
		public static void ConnectRooms(Room roomA, Room roomB)
		{
			if (roomA.isAccessibleFromMainRoom)
			{
				roomB.SetAccessibleFromMainRoom();
			}
			else if (roomB.isAccessibleFromMainRoom)
			{
				roomA.SetAccessibleFromMainRoom();
			}
			roomA.connectedRooms.Add(roomB);
			roomB.connectedRooms.Add(roomA);
		}

		/// Returns true if this room is directly connected to otherRoom.
		public bool IsConnected(Room otherRoom)
		{
			return connectedRooms.Contains(otherRoom);
		}

		/// Compares rooms by size in descending order (largest rooms sort first).
		public int CompareTo(Room otherRoom)
		{
			return otherRoom.roomSize.CompareTo(roomSize);
		}
	}
}