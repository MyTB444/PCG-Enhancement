using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Evaluation — implements Expressive Range Analysis (ERA) for the "Library of the Magus"
/// generator. Batch-generates maps from both Lague's original generator and our modified
/// generator, then measures structural metrics per map.
///
/// Results are written to Assets/evaluation_results.csv for external plotting.
///
/// To run: attach this component to the MapGenerator GameObject, right-click the component
/// in the Inspector → "Run Evaluation".
///
/// Stretch goal for the "Library of the Magus" coursework.
/// </summary>
public class Evaluation : MonoBehaviour
{
	[Header("References")]
	/// Reference to the MapGenerator. If left empty, the script will look for one
	/// on the same GameObject (via GetComponent).
	public MapGenerator mapGenerator;

	[Header("Evaluation parameters")]
	/// Number of maps to generate per generator configuration.
	public int sampleCount = 50;

	/// Base seed string; evaluation appends an integer to produce distinct seeds.
	public string seedBase = "eval";

	/// Output CSV path (relative to project root).
	public string outputPath = "Assets/evaluation_results.csv";

	/// <summary>
	/// Runs the full evaluation: for each generator configuration, generates
	/// sampleCount maps, computes metrics, and writes results to CSV.
	/// </summary>
	[ContextMenu("Run Evaluation")]
	public void RunEvaluation()
	{
		// Use assigned reference if provided; otherwise look on the same GameObject
		MapGenerator gen = mapGenerator;
		if (gen == null) gen = GetComponent<MapGenerator>();

		if (gen == null)
		{
			Debug.LogError("Evaluation: no MapGenerator assigned or found on this GameObject.");
			return;
		}

		StringBuilder csv = new StringBuilder();
		csv.AppendLine("generator,seed,openness,roomCount,meanRoomSize");

		// --- Configuration 1: Lague's original (useBSP = false) ---
		Debug.Log("Evaluation: running Lague original (" + sampleCount + " maps)...");
		for (int i = 0; i < sampleCount; i++)
		{
			string seed = seedBase + "_" + i;
			int[,] map = gen.GenerateMapForEvaluation(seed, false);
			var metrics = ComputeMetrics(map);
			csv.AppendLine("Lague," + seed + "," + metrics.openness.ToString("F3") +
				"," + metrics.roomCount + "," + metrics.meanRoomSize.ToString("F1"));
		}

		// --- Configuration 2: Our BSP generator (useBSP = true) ---
		Debug.Log("Evaluation: running BSP generator (" + sampleCount + " maps)...");
		for (int i = 0; i < sampleCount; i++)
		{
			string seed = seedBase + "_" + i;
			int[,] map = gen.GenerateMapForEvaluation(seed, true);
			var metrics = ComputeMetrics(map);
			csv.AppendLine("BSP," + seed + "," + metrics.openness.ToString("F3") +
				"," + metrics.roomCount + "," + metrics.meanRoomSize.ToString("F1"));
		}

		// Write CSV
		File.WriteAllText(outputPath, csv.ToString());
		Debug.Log("Evaluation: complete. Results written to " + outputPath);
	}

	/// <summary>
	/// Container for the three metrics we measure per map.
	/// </summary>
	struct MapMetrics
	{
		public float openness;     // fraction of empty cells
		public int roomCount;      // number of rooms above size threshold
		public float meanRoomSize; // average tiles per surviving room
	}

	/// <summary>
	/// Computes structural metrics for a given map grid.
	/// - openness: fraction of empty cells in the map (0.0 to 1.0)
	/// - roomCount: number of distinct connected empty regions > 50 tiles
	/// - meanRoomSize: average tiles per surviving room
	/// </summary>
	MapMetrics ComputeMetrics(int[,] map)
	{
		MapMetrics m = new MapMetrics();

		int w = map.GetLength(0);
		int h = map.GetLength(1);
		int totalCells = w * h;
		int emptyCells = 0;

		// Count empty cells
		for (int x = 0; x < w; x++)
		{
			for (int y = 0; y < h; y++)
			{
				if (map[x, y] == 0) emptyCells++;
			}
		}
		m.openness = (float)emptyCells / totalCells;

		// Find rooms via flood fill (similar to MapGenerator's internal logic)
		List<int> roomSizes = new List<int>();
		int[,] flags = new int[w, h];

		for (int x = 0; x < w; x++)
		{
			for (int y = 0; y < h; y++)
			{
				if (flags[x, y] == 0 && map[x, y] == 0)
				{
					int size = FloodFillSize(map, flags, x, y, w, h);
					if (size >= 50)
					{
						roomSizes.Add(size);
					}
				}
			}
		}

		m.roomCount = roomSizes.Count;
		if (roomSizes.Count > 0)
		{
			int sum = 0;
			foreach (int s in roomSizes) sum += s;
			m.meanRoomSize = (float)sum / roomSizes.Count;
		}
		else
		{
			m.meanRoomSize = 0;
		}

		return m;
	}

	/// <summary>
	/// BFS flood fill from (startX, startY) marking flags and returning region size.
	/// Only 4-directional moves (same as MapGenerator.GetRegionTiles).
	/// </summary>
	int FloodFillSize(int[,] map, int[,] flags, int startX, int startY, int w, int h)
	{
		int count = 0;
		Queue<Vector2Int> queue = new Queue<Vector2Int>();
		queue.Enqueue(new Vector2Int(startX, startY));
		flags[startX, startY] = 1;

		while (queue.Count > 0)
		{
			Vector2Int c = queue.Dequeue();
			count++;

			int[] dx = { 1, -1, 0, 0 };
			int[] dy = { 0, 0, 1, -1 };
			for (int i = 0; i < 4; i++)
			{
				int nx = c.x + dx[i];
				int ny = c.y + dy[i];
				if (nx >= 0 && nx < w && ny >= 0 && ny < h &&
					flags[nx, ny] == 0 && map[nx, ny] == 0)
				{
					flags[nx, ny] = 1;
					queue.Enqueue(new Vector2Int(nx, ny));
				}
			}
		}

		return count;
	}
}