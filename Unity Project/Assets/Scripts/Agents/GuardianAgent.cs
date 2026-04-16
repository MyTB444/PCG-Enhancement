using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Guardian Agent — a stone automaton bound to the tower by Erasmus, tasked with
/// protecting the library from intruders. It patrols a fixed route between anchor
/// points, investigating the player if spotted nearby, chasing if close, and
/// returning to patrol once the player escapes.
///
/// Behaviour tree (implemented as a finite state machine):
///   PATROL ── player enters alertDistance ──→ INVESTIGATE
///   INVESTIGATE ── player enters chaseDistance ──→ CHASE
///   CHASE ── player escapes alertDistance ──→ INVESTIGATE (searches last-seen pos)
///   INVESTIGATE ── timeout or arrived ──→ PATROL
/// </summary>
public class GuardianAgent : MonoBehaviour
{
	[Header("Movement")]
	public float patrolSpeed = 3f;
	public float investigateSpeed = 5f;
	public float chaseSpeed = 7f;

	[Header("Senses")]
	public float alertDistance = 12f;
	public float chaseDistance = 6f;
	public float investigateTimeout = 4f;

	[Header("Patrol")]
	public int patrolPointCount = 4;

	[Header("References")]
	public MapGenerator mapGenerator;

	// ----- Internal state -----
	private enum State { Patrol, Investigate, Chase }
	private State currentState = State.Patrol;

	private List<Vector3> patrolPoints = new List<Vector3>();
	private int patrolIndex = 0;
	private Vector3 investigateTarget;
	private float investigateTimer;

	private Transform playerTransform;
	private Rigidbody rb;

	void Start()
	{
		rb = GetComponent<Rigidbody>();
		GeneratePatrolRoute();
	}

	void Update()
	{
		// Refresh player reference every frame. The player is destroyed and respawned
		// on every map regeneration, so cached references become invalid.
		if (playerTransform == null)
		{
			GameObject p = GameObject.FindGameObjectWithTag("Player");
			if (p != null) playerTransform = p.transform;
		}

		// If patrol route is empty (e.g. spawned before map was ready), try again
		if (patrolPoints.Count == 0)
		{
			GeneratePatrolRoute();
		}

		// Perception
		float distToPlayer = float.MaxValue;
		if (playerTransform != null)
		{
			distToPlayer = Vector3.Distance(transform.position, playerTransform.position);
		}

		// State transitions
		if (distToPlayer < chaseDistance)
		{
			currentState = State.Chase;
		}
		else if (distToPlayer < alertDistance)
		{
			investigateTarget = playerTransform.position;
			investigateTimer = investigateTimeout;
			currentState = State.Investigate;
		}
		else if (currentState == State.Chase)
		{
			investigateTarget = playerTransform != null ? playerTransform.position : transform.position;
			investigateTimer = investigateTimeout;
			currentState = State.Investigate;
		}

		// State actions
		switch (currentState)
		{
			case State.Patrol: TickPatrol(); break;
			case State.Investigate: TickInvestigate(); break;
			case State.Chase: TickChase(); break;
		}
	}

	void TickPatrol()
	{
		if (patrolPoints.Count == 0) return;

		Vector3 target = patrolPoints[patrolIndex];
		Vector3 dir = (target - transform.position).normalized;
		dir.y = 0f;
		Move(dir, patrolSpeed);

		if (Vector3.Distance(transform.position, target) < 1.5f)
		{
			patrolIndex = (patrolIndex + 1) % patrolPoints.Count;
		}
	}

	void TickInvestigate()
	{
		Vector3 dir = (investigateTarget - transform.position).normalized;
		dir.y = 0f;
		Move(dir, investigateSpeed);

		investigateTimer -= Time.deltaTime;
		if (investigateTimer <= 0f || Vector3.Distance(transform.position, investigateTarget) < 1f)
		{
			currentState = State.Patrol;
		}
	}

	void TickChase()
	{
		if (playerTransform == null) return;
		Vector3 dir = (playerTransform.position - transform.position).normalized;
		dir.y = 0f;
		Move(dir, chaseSpeed);
	}

	void Move(Vector3 direction, float speed)
	{
		if (rb != null)
			rb.MovePosition(rb.position + direction * speed * Time.deltaTime);
		else
			transform.position += direction * speed * Time.deltaTime;
	}

	void GeneratePatrolRoute()
	{
		patrolPoints.Clear();
		if (mapGenerator == null || mapGenerator.currentMap == null) return;

		int[,] grid = mapGenerator.currentMap;
		int w = grid.GetLength(0);
		int h = grid.GetLength(1);

		for (int i = 0; i < patrolPointCount; i++)
		{
			for (int attempt = 0; attempt < 200; attempt++)
			{
				int rx = Random.Range(1, w - 1);
				int ry = Random.Range(1, h - 1);
				if (grid[rx, ry] == 0)
				{
					patrolPoints.Add(mapGenerator.GridToWorldPoint(rx, ry));
					break;
				}
			}
		}
	}
}