## Plan: Jellyfin EDL Plugin Subagent Waves

Deliver a Jellyfin plugin endpoint that accepts a library item ID and returns a same-folder EDL sidecar as text/plain when valid, with 404 for missing or not-eligible items and 422 for invalid EDL content. Work is split into dependency-aware waves so independent tracks run in parallel and merge cleanly.

### Scope and Constraints
- Included: plugin scaffold, DI wiring, item lookup flow, EDL parsing/validation, authenticated API route, logging, docs, and tests.
- Excluded: caching, admin UI/config pages, non-EDL sidecar formats, and broad media-segment feature expansion.
- Fixed decisions: latest stable Jellyfin target, authenticated users allowed, response content type text/plain.

### Wave Structure
1. Wave 0: Foundation (single subagent, blocking)
2. Wave 1: Core parallel build tracks (2 subagents in parallel)
3. Wave 2: API integration and behavior mapping (single subagent, depends on Wave 1)
4. Wave 3: Validation, packaging, docs hardening (single subagent, depends on Wave 2)

### Wave 0 Task: Project Foundation (blocking)
- Objective: create the minimal plugin skeleton and project references needed for all later work.
- Subagent instructions:
  1. Scaffold plugin project structure from repository root with clear folders for Api, Services, Configuration, and optionally Models.
  2. Add plugin entry class derived from BasePlugin<TConfig> and minimal configuration class.
  3. Add service registrator implementing IPluginServiceRegistrator with DI placeholders for upcoming services.
  4. Align package references and target framework with latest stable Jellyfin plugin conventions.
  5. Ensure build succeeds before handoff.
- Deliverables:
  1. Buildable plugin project skeleton.
  2. Plugin metadata and entrypoint.
  3. DI registration surface ready for lookup/parser services.
- Acceptance criteria:
  1. Project compiles with no errors.
  2. Plugin assembly contains controller-discoverable application part once controllers are added.
  3. DI registrator is invoked path-ready (register method implemented and wired).
- Required tests/checks:
  1. Local build in Release.
  2. Basic plugin load sanity check in Jellyfin instance if environment is available.

### Wave 1A Task: Item Lookup Service (parallel with Wave 1B)
- Objective: implement item-ID-to-EDL-path resolution with clear failure taxonomy.
- Subagent instructions:
  1. Implement service method accepting Guid itemId.
  2. Resolve item via ILibraryManager.
  3. Validate item is Video and local file-backed with usable primary path.
  4. Compute sibling .edl path by replacing media extension.
  5. Check file existence.
  6. Return typed result (success with paths or failure enum/reason object).
  7. Add concise logging hooks at decision points.
- Deliverables:
  1. Lookup service and result models.
  2. Unit tests for each failure reason and success path computation.
- Acceptance criteria:
  1. Missing item maps to not found outcome.
  2. Non-video maps to not eligible outcome.
  3. Non-local/no-path maps to not eligible outcome.
  4. Existing sidecar returns resolved path outcome.
- Required tests/checks:
  1. Unit tests for path replacement edge cases.
  2. Unit tests for every branch in resolution flow.

### Wave 1B Task: EDL Parser and Validator (parallel with Wave 1A)
- Objective: parse/validate common EDL variants and comment handling while preserving raw response requirement.
- Subagent instructions:
  1. Implement parser that accepts EDL text/lines and returns either valid result or diagnostics.
  2. Support common whitespace-delimited entries and comment lines.
  3. Validate numeric timing fields and action token validity.
  4. Capture line-numbered diagnostic messages for invalid entries.
  5. Keep parser independent from controller and filesystem concerns.
- Deliverables:
  1. Parser/validator service.
  2. Diagnostic model for invalid files.
  3. Unit tests covering valid and invalid variants.
- Acceptance criteria:
  1. Valid file passes validation with no diagnostics.
  2. Comment and blank lines are ignored safely.
  3. Invalid lines return deterministic diagnostics with line numbers.
- Required tests/checks:
  1. Table-driven parser tests for representative variants.
  2. Tests for malformed numbers, unsupported actions, and missing fields.

### Wave 2 Task: API Controller Integration (depends on Wave 1A and 1B)
- Objective: expose authenticated endpoint and map service outcomes to contract status codes.
- Subagent instructions:
  1. Implement controller based on BaseJellyfinApiController.
  2. Add authenticated GET route taking itemId.
  3. Call lookup service; map missing/not-eligible outcomes to 404.
  4. Read located file, run parser validation.
  5. Return 422 for invalid EDL with diagnostics payload.
  6. Return text/plain raw EDL body for valid files.
  7. Ensure response content type and status code contract are explicit and tested.
- Deliverables:
  1. Controller endpoint and request/response models.
  2. Integration-style tests with mocked services.
- Acceptance criteria:
  1. Auth required for endpoint access.
  2. Success returns 200 text/plain and exact file content.
  3. Missing/not-eligible returns 404.
  4. Invalid EDL returns 422 with structured diagnostics.
- Required tests/checks:
  1. Controller tests for each status mapping branch.
  2. Auth behavior test (unauthenticated rejected).

### Wave 3 Task: Validation, Packaging, and Documentation (depends on Wave 2)
- Objective: harden release readiness and document endpoint behavior for users/contributors.
- Subagent instructions:
  1. Verify package/version alignment for latest stable Jellyfin.
  2. Run full test suite and ensure green.
  3. Execute end-to-end manual checks against Jellyfin item IDs.
  4. Update README with endpoint route, auth requirement, EDL rules, and status code matrix.
  5. Document known limitations and non-goals.
- Deliverables:
  1. Updated documentation.
  2. Final validation notes/checklist.
- Acceptance criteria:
  1. Build and tests pass in clean environment.
  2. Plugin loads and endpoint is discoverable.
  3. README accurately reflects implemented behavior.
- Required tests/checks:
  1. End-to-end request checks for success, missing EDL, invalid EDL, and non-video item.
  2. Smoke check for plugin startup errors/log warnings.

### Cross-Wave Merge and Coordination Rules
- Wave 1A and Wave 1B must not modify the same files except DI registration touchpoints coordinated in short, isolated commits.
- Define stable interfaces before parallel execution so controller integration has minimal rework.
- Use explicit failure/result contracts shared between services and controller.
- Resolve branch conflicts by preserving status-code contract and parser diagnostics semantics.

### Global Acceptance Criteria
1. Endpoint accepts item ID and follows pipeline: resolve item, verify video/local path, locate sibling EDL, validate EDL, return raw EDL.
2. Status behavior is exact: 200 success, 404 missing or not-eligible, 422 invalid EDL.
3. Endpoint requires authenticated users and allows non-admin authenticated access.
4. Tests exist for service logic, parser logic, and controller mapping behavior.
5. README documents route contract, auth, EDL handling, and limitations.
