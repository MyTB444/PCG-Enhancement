using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Wisp Agent — a tiny enchanted light left behind by Erasmus to guide whomever
/// carries his key. It orbits the player at a short distance, offering gentle
/// guidance toward points of interest (landmarks, columns) in the current chamber.
///
/// Behaviour tree (implemented as a finite state machine):
///   FOLLOW      ── landmark nearby         ──→ POINT
///   POINT       ── landmark reached or gone ──→ FOLLOW
///   FOLLOW/POINT ── player too far         ──→ CATCHUP
///   CATCHUP     ── reunited                 ──→ FOLLOW
/// </summary>
public class WispAgent : MonoBehaviour
{
	[Header("Movement")]
	public float followSpeed = 5f;
	public float pointSpeed = 3f;
	public float catchupSpeed = 12f;

	[Header("Behaviour")]
	public float followDistance = 3f;
	public float leashDistance = 15f;
	public float landmarkScanRadius = 8f;

	[Header("References")]
	public MapGenerator mapGenerator;

	// ----- Internal state -----
	private enum State { Follow, Point, Catchup }
	private State currentState = State.Follow;

	private float orbitAngle;
	private Vector3 landmarkTarget;

	private Transform playerTransform;
	private Rigidbody rb;

	/// Called once on spawn. Caches the Rigidbody and sets a random starting
	/// orbit angle so multiple wisps don't orbit in lockstep.
	void Start()
	{
		rb = GetComponent<Rigidbody>();
		orbitAngle = Random.Range(0f, 360f);
	}

	/// Per-frame tick. Refreshes the player reference, selects the appropriate
	/// state based on distance and landmark proximity, and dispatches to the
	/// state action.
	void Update()
	{
		// Refresh player reference every frame. The player is destroyed and
		// respawned on every map regeneration.
		if (playerTransform == null)
		{
			GameObject p = GameObject.FindGameObjectWithTag("Player");
			if (p != null) playerTransform = p.transform;
		}

		if (playerTransform == null) return;

		float distToPlayer = Vector3.Distance(transform.position, playerTransform.position);

		// State transitions
		if (distToPlayer > leashDistance)
		{
			currentState = State.Catchup;
		}
		else if (FindNearestLandmark(out landmarkTarget))
		{
			currentState = State.Point;
		}
		else
		{
			currentState = State.Follow;
		}

		switch (currentState)
		{
			case State.Follow: TickFollow(); break;
			case State.Point: TickPoint(); break;
			case State.Catchup: TickCatchup(); break;
		}
	}

	/// Follow action: orbit the player at followDistance, rotating around them.
	/// The orbit angle advances each frame so the wisp visibly circles the player.
	void TickFollow()
	{
		orbitAngle += 45f * Time.deltaTime;
		Vector3 orbitOffset = new Vector3(
			Mathf.Cos(orbitAngle * Mathf.Deg2Rad) * followDistance,
			0f,
			Mathf.Sin(orbitAngle * Mathf.Deg2Rad) * followDistance);

		Vector3 target = playerTransform.position + orbitOffset;
		Vector3 dir = (target - transform.position).normalized;
		dir.y = 0f;
		Move(dir, followSpeed);
	}

	/// Point action: drift toward the detected landmark to draw the player's attention.
	void TickPoint()
	{
		Vector3 dir = (landmarkTarget - transform.position).normalized;
		dir.y = 0f;
		Move(dir, pointSpeed);
	}

	/// Catchup action: move quickly directly toward the player. Used when the
	/// wisp has drifted or been separated by more than leashDistance.
	void TickCatchup()
	{
		Vector3 dir = (playerTransform.position - transform.position).normalized;
		dir.y = 0f;
		Move(dir, catchupSpeed);
	}

	/// Applies movement via Rigidbody where available, otherwise via transform.
	void Move(Vector3 direction, float speed)
	{
		if (rb != null)
			rb.MovePosition(rb.position + direction * speed * Time.deltaTime);
		else
			transform.position += direction * speed * Time.deltaTime;
	}

	/// Scans the map for a wall cell near the player that looks like a 3x3 column
	/// (has >= 4 wall neighbours). Returns true and sets 'target' to its world position.
	bool FindNearestLandmark(out Vector3 target)
	{
		target = Vector3.zero;
		if (mapGenerator == null || mapGenerator.currentMap == null) return false;

		int[,] grid = mapGenerator.currentMap;
		int w = grid.GetLength(0);
		int h = grid.GetLength(1);

		// Convert player world position to grid coordinates
		int px = Mathf.RoundToInt(playerTransform.position.x + w / 2f - 0.5f);
		int py = Mathf.RoundToInt(playerTransform.position.z + h / 2f - 0.5f);

		int scanCells = Mathf.CeilToInt(landmarkScanRadius);
		int bestGx = -1, bestGy = -1;
		float bestDist = float.MaxValue;

		for (int dx = -scanCells; dx <= scanCells; dx++)
		{
			for (int dy = -scanCells; dy <= scanCells; dy++)
			{
				int gx = px + dx;
				int gy = py + dy;
				if (gx < 1 || gx >= w - 1 || gy < 1 || gy >= h - 1) continue;
				if (grid[gx, gy] != 1) continue;

				int wallCount = 0;
				for (int nx = gx - 1; nx <= gx + 1; nx++)
					for (int ny = gy - 1; ny <= gy + 1; ny++)
						if (nx != gx || ny != gy)
							if (grid[nx, ny] == 1) wallCount++;

				if (wallCount >= 4)
				{
					float d = dx * dx + dy * dy;
					if (d < bestDist)
					{
						bestDist = d;
						bestGx = gx;
						bestGy = gy;
					}
				}
			}
		}

		if (bestGx >= 0)
		{
			target = mapGenerator.GridToWorldPoint(bestGx, bestGy);
			return true;
		}
		return false;
	}
}