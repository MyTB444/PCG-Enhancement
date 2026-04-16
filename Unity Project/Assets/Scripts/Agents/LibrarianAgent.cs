using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Librarian Agent — a spectral spirit of a past reader, drifting between library
/// chambers. Represents the ghosts of scholars who never left Erasmus's tower.
///
/// Behaviour tree (implemented as a finite state machine):
///   IDLE   → (wait timer expires)     → DRIFT   → (reached target) → IDLE
///   Any    → (player comes too close) → FADE    → (player far)     → IDLE
///
/// The librarian picks a random room and drifts slowly toward it, lingering at
/// each shelf. If the player approaches, it "fades" — retreats in the opposite
/// direction — reflecting the timid nature of a fading memory.
/// </summary>
public class LibrarianAgent : MonoBehaviour
{
	[Header("Movement")]
	public float driftSpeed = 3f;
	public float fadeSpeed = 6f;

	[Header("Behaviour")]
	public float fadeDistance = 7f;
	public float idleTime = 3f;

	[Header("References")]
	public MapGenerator mapGenerator;

	// ----- Internal state -----
	private enum State { Idle, Drift, Fade }
	private State currentState = State.Idle;
	private Vector3 targetPosition;
	private float idleTimer;
	private Transform playerTransform;
	private Rigidbody rb;

	void Start()
	{
		rb = GetComponent<Rigidbody>();
		idleTimer = idleTime;
		targetPosition = transform.position;
	}

	void Update()
	{
		// Refresh player reference every frame. The player GameObject is destroyed
		// and respawned on every map regeneration, so a cached reference from Start()
		// becomes null. Re-finding by tag keeps the agent functional across levels.
		if (playerTransform == null)
		{
			GameObject p = GameObject.FindGameObjectWithTag("Player");
			if (p != null) playerTransform = p.transform;
		}

		// Behaviour-tree selector: fade condition overrides any other state
		if (playerTransform != null)
		{
			float distToPlayer = Vector3.Distance(transform.position, playerTransform.position);
			if (distToPlayer < fadeDistance)
			{
				currentState = State.Fade;
			}
			else if (currentState == State.Fade)
			{
				currentState = State.Idle;
				idleTimer = idleTime * 0.5f;
			}
		}

		switch (currentState)
		{
			case State.Idle: TickIdle(); break;
			case State.Drift: TickDrift(); break;
			case State.Fade: TickFade(); break;
		}
	}

	void TickIdle()
	{
		idleTimer -= Time.deltaTime;
		if (idleTimer <= 0f)
		{
			targetPosition = GetRandomOpenPosition();
			currentState = State.Drift;
		}
	}

	void TickDrift()
	{
		Vector3 dir = (targetPosition - transform.position).normalized;
		dir.y = 0f;
		Move(dir, driftSpeed);

		if (Vector3.Distance(transform.position, targetPosition) < 1f)
		{
			currentState = State.Idle;
			idleTimer = idleTime;
		}
	}

	void TickFade()
	{
		if (playerTransform == null) return;
		Vector3 awayDir = (transform.position - playerTransform.position).normalized;
		awayDir.y = 0f;
		Move(awayDir, fadeSpeed);
	}

	void Move(Vector3 direction, float speed)
	{
		if (rb != null)
			rb.MovePosition(rb.position + direction * speed * Time.deltaTime);
		else
			transform.position += direction * speed * Time.deltaTime;
	}

	Vector3 GetRandomOpenPosition()
	{
		if (mapGenerator != null && mapGenerator.currentMap != null)
		{
			int[,] grid = mapGenerator.currentMap;
			int w = grid.GetLength(0);
			int h = grid.GetLength(1);

			for (int attempt = 0; attempt < 100; attempt++)
			{
				int rx = Random.Range(1, w - 1);
				int ry = Random.Range(1, h - 1);
				if (grid[rx, ry] == 0)
					return mapGenerator.GridToWorldPoint(rx, ry);
			}
		}
		return transform.position + new Vector3(Random.Range(-10f, 10f), 0f, Random.Range(-10f, 10f));
	}
}