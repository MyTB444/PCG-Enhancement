using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using System.Linq;

/// <summary>
/// Procedural level generator based on cellular automata.
/// Original code by Sebastian Lague (Procedural Cave Generation tutorial).
/// Modified for "Verdant Labyrinth" — a forest-themed exploration game.
/// 
/// Modifications:
///   Stage 1: Perlin noise-based cell initialisation (replaces pure random fill).
///   Stage 3: Enhanced post-processing with clearings, entrance structures,
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

	// ----- NEW CODE: Stage 1 — Perlin Noise Parameters -----
	// These parameters control the fractal Perlin noise used to initialise the map
	// grid, producing organic terrain shapes instead of uniform random noise.

	[Header("NEW — Stage 1: Perlin Noise Initialisation")]

	/// When true, uses Perlin noise initialisation; when false, falls back to original random fill.
	public bool usePerlinNoise = true;

	/// Controls the zoom level of the Perlin noise sampling. Larger values produce
	/// broader, smoother features; smaller values produce finer detail.
	public float noiseScale = 18f;

	/// Number of layered noise samples (octaves). More octaves add finer detail
	/// on top of the base shape, creating more natural-looking terrain.
	[Range(1, 8)]
	public int octaves = 4;

	/// Controls how much each successive octave contributes to the final value.
	/// Lower values make higher octaves fade out quickly (smoother result).
	[Range(0f, 1f)]
	public float persistence = 0.5f;

	/// Controls how much the frequency increases per octave. Higher values add
	/// more high-frequency detail in each successive layer.
	public float lacunarity = 2.0f;

	/// Noise values above this threshold become walls (1); values at or below become
	/// empty space (0). Adjusting this shifts the balance between open and dense areas.
	[Range(0.3f, 0.7f)]
	public float fillThreshold = 0.46f;

	/// Strength of the radial edge gradient. Higher values force more walls near the
	/// map edges, creating a natural border of dense forest around the playable area.
	[Range(0f, 0.8f)]
	public float edgeDensity = 0.35f;

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

	/// Percentage chance (0-100) that a landmark (small wall cluster) is placed at an
	/// eligible position within a large room, adding visual and gameplay variety.
	[Range(0, 30)]
	public int landmarkChance = 10;

	/// Minimum radius of passages carved between connected rooms.
	[Range(1, 5)]
	public int minPassageWidth = 2;

	/// Maximum radius of passages carved between rooms connected to the main room.
	/// Wider passages create a clear hierarchy of major and minor paths.
	[Range(3, 10)]
	public int maxPassageWidth = 6;

	/// NEW CODE: half-size of the rectangular entrance structure placed in each room.
	/// A value of 3 creates a 7x7 outline (2*3+1). Represents ruined shrine foundations.
	[Range(2, 5)]
	public int structureHalfSize = 3;

	/// NEW CODE: width of the entrance opening in the structure (in cells).
	/// The opening faces toward the room's nearest passage connection.
	[Range(1, 5)]
	public int structureGapWidth = 3;

	// ----- Private state -----

	/// Reference to the currently spawned player GameObject, destroyed on regeneration.
	private GameObject playerInstance;

	/// The 2D grid representing the level. 0 = empty/path, 1 = wall/thicket.
	int[,] map;

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
		// NEW CODE: choose between Perlin noise and original random fill
		if (usePerlinNoise)
		{
			PerlinFillMap();   // NEW CODE: Perlin noise initialisation
		}
		else
		{
			RandomFillMap();   // Original random fill (kept as fallback for evaluation)
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
	/// Fills the map grid using layered Perlin noise (fractal Brownian motion)
	/// combined with a radial edge gradient.
	///
	/// Technique reference: fractal Brownian motion (fBm) is a standard PCG
	/// technique combining multiple octaves of coherent noise at increasing
	/// frequencies to produce natural-looking terrain. See Shaker, Shaker and
	/// Togelius, "Procedural Content Generation in Games" (Springer, 2016).
	///
	/// The radial gradient ensures map edges are predominantly walls, creating
	/// a natural border of dense forest while keeping the interior more open.
	/// </summary>
	void PerlinFillMap()
	{
		// Derive a deterministic seed for noise offsets
		if (useRandomSeed)
		{
			seed = Time.time.ToString();
		}

		System.Random pseudoRandom = new System.Random(seed.GetHashCode());

		// NEW CODE: randomised offsets ensure each seed produces a unique noise pattern.
		// Without offsets, Perlin noise always samples the same region of noise space.
		float offsetX = pseudoRandom.Next(-100000, 100000);
		float offsetY = pseudoRandom.Next(-100000, 100000);

		// Precompute the max distance from centre for gradient normalisation
		float maxDist = Mathf.Sqrt((width / 2f) * (width / 2f) + (height / 2f) * (height / 2f));

		for (int x = 0; x < width; x++)
		{
			for (int y = 0; y < height; y++)
			{
				// Force the outermost ring of cells to be walls, ensuring a sealed boundary
				if (x == 0 || x == width - 1 || y == 0 || y == height - 1)
				{
					map[x, y] = 1;
					continue;
				}

				// NEW CODE: accumulate multiple octaves of Perlin noise (fBm)
				float noiseValue = 0f;
				float amplitude = 1f;
				float frequency = 1f;
				float maxAmplitude = 0f;

				for (int o = 0; o < octaves; o++)
				{
					// Sample coordinates scaled by frequency and offset by seed
					float sampleX = (x + offsetX) / noiseScale * frequency;
					float sampleY = (y + offsetY) / noiseScale * frequency;

					float perlin = Mathf.PerlinNoise(sampleX, sampleY);
					noiseValue += perlin * amplitude;
					maxAmplitude += amplitude;

					// Each successive octave has reduced amplitude and increased frequency
					amplitude *= persistence;
					frequency *= lacunarity;
				}

				// Normalise to 0-1 range
				noiseValue /= maxAmplitude;

				// NEW CODE: radial gradient — cells further from the centre are biased
				// toward becoming walls, creating a natural dense-forest border.
				float distFromCentre = Mathf.Sqrt(
					Mathf.Pow(x - width / 2f, 2) + Mathf.Pow(y - height / 2f, 2)
				);
				float normalizedDist = distFromCentre / maxDist;
				noiseValue += normalizedDist * edgeDensity;

				// Threshold: values above fillThreshold become walls (dense forest),
				// values at or below become empty space (paths/clearings)
				map[x, y] = (noiseValue > fillThreshold) ? 1 : 0;
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
	///   - PlaceRoomStructures(): places a rectangular entrance in every room.
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

		// NEW CODE: carve circular clearings at the centres of large rooms,
		// transforming amorphous CA shapes into distinct forest clearings.
		CreateClearings(survivingRooms);

		// NEW CODE: place a rectangular entrance/shrine structure in every room,
		// representing ruins of the forgotten civilisation overgrown by the forest.
		PlaceRoomStructures(survivingRooms);

		// NEW CODE: place small wall clusters (landmarks) inside large clearings
		// to represent ancient trees, stone formations, or ruins.
		PlaceLandmarks(survivingRooms);

		// NEW CODE: apply a gentle smoothing pass to passage edges,
		// creating more organic, winding forest paths rather than perfectly round tunnels.
		SmoothPassageEdges();

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
	/// Places a rectangular wall outline (resembling a ruined entrance or shrine
	/// foundation) in every surviving room. The structure is a square perimeter of
	/// wall cells with a gap on one side acting as an opening/entrance.
	///
	/// The opening faces toward the room's nearest connected room, creating a sense
	/// of directionality — the entrance points toward the corridor leading to the
	/// next room. The structure is placed at the room's centroid, ensuring it sits
	/// inside the clearing carved by CreateClearings().
	///
	/// Safety checks ensure the structure only appears if the surrounding area is
	/// fully open, preventing it from merging with existing walls.
	///
	/// This gives every room a recognisable architectural focal point, reinforcing
	/// the game concept of a forest grown over ancient ruins.
	/// </summary>
	void PlaceRoomStructures(List<Room> rooms)
	{
		foreach (Room room in rooms)
		{
			// Calculate centroid of this room
			float centreX = 0;
			float centreY = 0;
			foreach (Coord tile in room.tiles)
			{
				centreX += tile.tileX;
				centreY += tile.tileY;
			}
			centreX /= room.tiles.Count;
			centreY /= room.tiles.Count;

			int cx = Mathf.RoundToInt(centreX);
			int cy = Mathf.RoundToInt(centreY);

			// Check the structure fits within the map bounds (footprint + 1-cell margin)
			int s = structureHalfSize;
			if (!IsInMapRange(cx - s - 1, cy - s - 1) || !IsInMapRange(cx + s + 1, cy + s + 1))
			{
				continue;
			}

			// Verify the entire footprint plus a 1-cell border is open space,
			// so the structure does not merge with existing walls
			if (!HasClearNeighbourhood(cx, cy, s + 1))
			{
				continue;
			}

			// Determine which side the opening faces: toward the nearest connected room
			// 0 = south (bottom), 1 = north (top), 2 = west (left), 3 = east (right)
			int openSide = GetOpeningSide(room, cx, cy);

			// Build the rectangular outline with an opening on one side
			for (int x = cx - s; x <= cx + s; x++)
			{
				for (int y = cy - s; y <= cy + s; y++)
				{
					// Only place walls on the perimeter of the rectangle
					bool isPerimeter = (x == cx - s || x == cx + s || y == cy - s || y == cy + s);
					if (!isPerimeter)
					{
						continue;
					}

					// Leave a gap on the opening side to form the entrance
					if (IsInGap(x, y, cx, cy, s, openSide))
					{
						continue;
					}

					if (IsInMapRange(x, y))
					{
						map[x, y] = 1;
					}
				}
			}
		}
	}

	/// <summary>
	/// NEW CODE — helper for PlaceRoomStructures.
	/// Determines which side of the structure should have the opening by finding
	/// the direction toward the nearest connected room's centroid.
	/// Returns 0 (south), 1 (north), 2 (west), or 3 (east).
	///
	/// This makes the entrance face the corridor leading to the closest neighbour,
	/// so the structure feels integrated into the level's navigation flow.
	/// </summary>
	int GetOpeningSide(Room room, int cx, int cy)
	{
		if (room.connectedRooms == null || room.connectedRooms.Count == 0)
		{
			return 0; // Default to south if no connections exist
		}

		// Find the centroid of the nearest connected room
		float nearestDist = float.MaxValue;
		float bestDx = 0;
		float bestDy = 0;

		foreach (Room connected in room.connectedRooms)
		{
			float connCx = 0, connCy = 0;
			foreach (Coord tile in connected.tiles)
			{
				connCx += tile.tileX;
				connCy += tile.tileY;
			}
			connCx /= connected.tiles.Count;
			connCy /= connected.tiles.Count;

			float dist = (connCx - cx) * (connCx - cx) + (connCy - cy) * (connCy - cy);
			if (dist < nearestDist)
			{
				nearestDist = dist;
				bestDx = connCx - cx;
				bestDy = connCy - cy;
			}
		}

		// The opening faces the dominant axis direction toward the nearest room
		if (Mathf.Abs(bestDx) > Mathf.Abs(bestDy))
		{
			return bestDx > 0 ? 3 : 2; // East or West
		}
		else
		{
			return bestDy > 0 ? 1 : 0; // North or South
		}
	}

	/// <summary>
	/// NEW CODE — helper for PlaceRoomStructures.
	/// Returns true if the cell (x, y) falls within the entrance gap on the
	/// specified side of the structure. The gap is centred on that side's edge
	/// and has a width of structureGapWidth cells.
	/// </summary>
	bool IsInGap(int x, int y, int cx, int cy, int halfSize, int openSide)
	{
		int halfGap = structureGapWidth / 2;

		switch (openSide)
		{
			case 0: // South — gap on the bottom edge (y == cy - halfSize)
				return y == cy - halfSize && x >= cx - halfGap && x <= cx + halfGap;
			case 1: // North — gap on the top edge (y == cy + halfSize)
				return y == cy + halfSize && x >= cx - halfGap && x <= cx + halfGap;
			case 2: // West — gap on the left edge (x == cx - halfSize)
				return x == cx - halfSize && y >= cy - halfGap && y <= cy + halfGap;
			case 3: // East — gap on the right edge (x == cx + halfSize)
				return x == cx + halfSize && y >= cy - halfGap && y <= cy + halfGap;
		}
		return false;
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
			// Only place landmarks in rooms large enough to accommodate them
			if (room.roomSize < clearingMinRoomSize * 1.5f)
			{
				continue;
			}

			foreach (Coord tile in room.tiles)
			{
				// Skip tiles on the room boundary to avoid narrowing passages
				if (IsEdgeTile(tile.tileX, tile.tileY))
				{
					continue;
				}

				// Check that the tile has a wide empty neighbourhood (5x5 clear area)
				// to avoid placing landmarks in narrow corridors
				if (!HasClearNeighbourhood(tile.tileX, tile.tileY, 2))
				{
					continue;
				}

				// Random chance to place a landmark at this position
				if (prng.Next(0, 100) < landmarkChance)
				{
					map[tile.tileX, tile.tileY] = 1;
				}
			}
		}
	}

	/// <summary>
	/// NEW CODE — helper for PlaceLandmarks.
	/// Returns true if the cell at (x, y) is adjacent to at least one wall cell,
	/// meaning it lies on the boundary between open and wall regions.
	/// </summary>
	bool IsEdgeTile(int x, int y)
	{
		for (int nx = x - 1; nx <= x + 1; nx++)
		{
			for (int ny = y - 1; ny <= y + 1; ny++)
			{
				if (nx == x && ny == y) continue;
				if (!IsInMapRange(nx, ny) || map[nx, ny] == 1)
				{
					return true;
				}
			}
		}
		return false;
	}

	/// <summary>
	/// NEW CODE — helper for PlaceLandmarks and PlaceRoomStructures.
	/// Returns true if all cells within a square of the given radius around (x, y)
	/// are empty (0). Used to ensure landmarks and structures are placed only
	/// in spacious areas where they will not block movement.
	/// </summary>
	bool HasClearNeighbourhood(int x, int y, int radius)
	{
		for (int nx = x - radius; nx <= x + radius; nx++)
		{
			for (int ny = y - radius; ny <= y + radius; ny++)
			{
				if (!IsInMapRange(nx, ny) || map[nx, ny] == 1)
				{
					return false;
				}
			}
		}
		return true;
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
	/// Spawns the player at a random empty tile. Destroys any previous player
	/// instance first to prevent duplicates on map regeneration.
	/// NEW CODE: prefers spawning in the main room (largest room) for a better
	/// starting position, falling back to any empty tile if needed.
	/// </summary>
	void SpawnPlayer()
	{
		if (playerInstance != null)
		{
			Destroy(playerInstance);
		}

		// NEW CODE: try to spawn in the main room first
		List<Coord> spawnCandidates = new List<Coord>();

		if (currentRooms != null && currentRooms.Count > 0)
		{
			Room mainRoom = currentRooms.Find(r => r.isMainRoom);
			if (mainRoom != null)
			{
				foreach (Coord tile in mainRoom.tiles)
				{
					if (!IsEdgeTile(tile.tileX, tile.tileY))
					{
						spawnCandidates.Add(tile);
					}
				}
			}
		}

		// Fallback: use all empty tiles if no main room candidates found
		if (spawnCandidates.Count == 0)
		{
			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					if (map[x, y] == 0)
					{
						spawnCandidates.Add(new Coord(x, y));
					}
				}
			}
		}

		Coord spawn = spawnCandidates[UnityEngine.Random.Range(0, spawnCandidates.Count)];
		Vector3 spawnPos = CoordToWorldPoint(spawn);
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
	public int[,] GenerateMapForEvaluation(string evalSeed, bool usePerlin)
	{
		seed = evalSeed;
		useRandomSeed = false;
		map = new int[width, height];

		bool originalSetting = usePerlinNoise;
		usePerlinNoise = usePerlin;

		if (usePerlinNoise)
		{
			PerlinFillMap();
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

		usePerlinNoise = originalSetting;
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