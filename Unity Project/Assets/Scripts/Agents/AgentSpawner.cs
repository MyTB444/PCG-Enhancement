using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// AgentSpawner — spawns all three agent types (Librarian, Guardian, Wisp) at
/// valid positions every time a new level is generated.
///
/// Must be attached to the same GameObject as MapGenerator. MapGenerator calls
/// OnMapGenerated() at the end of each generation, which triggers a respawn.
/// This guarantees agents spawn AFTER the mesh and player are ready.
/// </summary>
public class AgentSpawner : MonoBehaviour
{
	[Header("Prefabs")]
	/// Prefab for the Librarian agent (spectral reader that drifts between rooms).
	public GameObject librarianPrefab;
	/// Prefab for the Guardian agent (stone automaton patrolling the tower).
	public GameObject guardianPrefab;
	/// Prefab for the Wisp agent (magical light that orbits the player).
	public GameObject wispPrefab;

	[Header("Counts")]
	/// How many librarians to spawn per level.
	public int librarianCount = 3;
	/// How many guardians to spawn per level.
	public int guardianCount = 2;
	/// How many wisps to spawn per level (usually 1; it orbits the player).
	public int wispCount = 1;

	[Header("Spawn Rules")]
	/// Minimum world-space distance between a spawned agent and the player.
	/// Prevents agents from spawning on top of the player's exit room.
	public float minDistanceFromPlayer = 8f;

	[Header("References")]
	/// Reference to the MapGenerator. Auto-found if on the same GameObject.
	public MapGenerator mapGenerator;

	// ----- Internal state -----
	/// List of currently spawned agent GameObjects. Cleared on each regeneration.
	private List<GameObject> spawnedAgents = new List<GameObject>();

	void Awake()
	{
		if (mapGenerator == null)
		{
			mapGenerator = GetComponent<MapGenerator>();
		}
	}

	/// <summary>
	/// Called by MapGenerator at the end of GenerateMap(). Destroys old agents
	/// and spawns a fresh set at random valid positions on the new map.
	/// </summary>
	public void OnMapGenerated()
	{
		ClearAgents();
		SpawnAllAgents();
	}

	/// Destroys all currently spawned agents.
	void ClearAgents()
	{
		foreach (GameObject a in spawnedAgents)
		{
			if (a != null) Destroy(a);
		}
		spawnedAgents.Clear();
	}

	/// Spawns the configured number of each agent type.
	void SpawnAllAgents()
	{
		for (int i = 0; i < librarianCount; i++) SpawnOne(librarianPrefab);
		for (int i = 0; i < guardianCount; i++) SpawnOne(guardianPrefab);
		for (int i = 0; i < wispCount; i++) SpawnOne(wispPrefab);
	}

	/// Instantiates one agent prefab at a random valid position on the map.
	/// Injects the MapGenerator reference into whichever agent component is on the prefab.
	void SpawnOne(GameObject prefab)
	{
		if (prefab == null) return;

		Vector3 pos;
		if (!FindRandomOpenPosition(out pos)) return;

		GameObject agent = Instantiate(prefab, pos, Quaternion.identity);
		spawnedAgents.Add(agent);

		// Inject the MapGenerator reference into whichever agent script is attached
		LibrarianAgent lib = agent.GetComponent<LibrarianAgent>();
		if (lib != null) lib.mapGenerator = mapGenerator;

		GuardianAgent gd = agent.GetComponent<GuardianAgent>();
		if (gd != null) gd.mapGenerator = mapGenerator;

		WispAgent wisp = agent.GetComponent<WispAgent>();
		if (wisp != null) wisp.mapGenerator = mapGenerator;
	}

	/// Finds a random empty tile on the map grid that is at least
	/// minDistanceFromPlayer world units away from the player.
	/// Returns false only if no valid position could be found.
	bool FindRandomOpenPosition(out Vector3 pos)
	{
		pos = Vector3.zero;
		if (mapGenerator == null || mapGenerator.currentMap == null) return false;

		int[,] grid = mapGenerator.currentMap;
		int w = grid.GetLength(0);
		int h = grid.GetLength(1);

		GameObject player = GameObject.FindGameObjectWithTag("Player");
		Vector3 playerPos = player != null ? player.transform.position : Vector3.zero;
		bool hasPlayer = (player != null);

		// First try: 200 random tiles with distance check
		for (int attempt = 0; attempt < 200; attempt++)
		{
			int rx = Random.Range(2, w - 2);
			int ry = Random.Range(2, h - 2);

			if (grid[rx, ry] != 0) continue;

			Vector3 candidate = mapGenerator.GridToWorldPoint(rx, ry);

			if (hasPlayer && Vector3.Distance(candidate, playerPos) < minDistanceFromPlayer)
			{
				continue;
			}

			pos = candidate;
			return true;
		}

		// Fallback: accept any empty tile if distance check failed too many times
		for (int x = 2; x < w - 2; x++)
		{
			for (int y = 2; y < h - 2; y++)
			{
				if (grid[x, y] == 0)
				{
					pos = mapGenerator.GridToWorldPoint(x, y);
					return true;
				}
			}
		}

		return false;
	}
}