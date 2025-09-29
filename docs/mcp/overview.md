# MCP Overview

The Maintenance & Communication Plan (MCP) keeps all contributors aligned on how BrakeDiscInspector evolves. It complements the architectural, data-format and API guides by clarifying responsibilities, coordination workflows and artefact ownership.

## Quick index

- [Scope](#scope)
- [Roles & Responsibilities](#roles--responsibilities)
- [Change Management Workflow](#change-management-workflow)
- [Artefact Registry](#artefact-registry)
- [Model & Dataset Update Playbook](#model--dataset-update-playbook)
- [Logging & Observability Coordination](#logging--observability-coordination)
- [Release Cadence](#release-cadence)
- [Maintaining this Folder](#maintaining-this-folder)

## Scope

The MCP covers every change that can affect:

- The FastAPI backend in [`backend/`](../../backend) and its contracts (`/health`, `/fit_ok`, `/calibrate_ng`, `/infer`).
- The WPF GUI in [`gui/`](../../gui) that manages ROI datasets and interacts with the backend.
- Shared datasets, trained artefacts under `backend/models/<role>/<roi>/`, and calibration thresholds persisted in `calib.json`.
- Operational tooling (PowerShell scripts, logging, deployment flows) referenced across the repository.

## Roles & Responsibilities

| Role | Primary Responsibilities | Reference Material |
|------|--------------------------|--------------------|
| MCP Maintainer | Curate this documentation, track action items, publish updates in [`latest_updates.md`](latest_updates.md). | This folder |
| Backend Lead | Own API/contract stability, PatchCore artefacts, deployment readiness. | [`API_REFERENCE.md`](../../API_REFERENCE.md), [`backend/README_backend.md`](../../backend/README_backend.md) |
| GUI Lead | Coordinate GUI release cadence, ROI tooling updates, dataset UX. | [`ARCHITECTURE.md`](../../ARCHITECTURE.md), [`DEV_GUIDE.md`](../../DEV_GUIDE.md) |
| Data Steward | Version datasets, manage `models/<role>/<roi>/memory.npz` and `calib.json`, ensure mm/px consistency. | [`DATA_FORMATS.md`](../../DATA_FORMATS.md), [`ROI_AND_MATCHING_SPEC.md`](../../ROI_AND_MATCHING_SPEC.md) |

## Change Management Workflow

1. **Propose** the change via issue or PR, referencing impacted specs (architecture, data formats, logging, deployment).
2. **Assess impact** with relevant role owners (backend, GUI, data). Confirm compatibility with existing artefacts.
3. **Validate** using procedures in [`DEV_GUIDE.md`](../../DEV_GUIDE.md) and smoke tests from [`DEPLOYMENT.md`](../../DEPLOYMENT.md).
4. **Document** the outcome:
   - Update the pertinent guide(s).
   - Record the summary in [`latest_updates.md`](latest_updates.md) with date, owner, links to evidence.
5. **Communicate** the release to the wider team (Slack/email) with links to the updated markdown files.

## Artefact Registry

- **PatchCore memory**: `backend/models/<role>/<roi>/memory.npz` (+ optional `index.faiss`).
- **Calibration**: `backend/models/<role>/<roi>/calib.json`.
- **Dataset exports**: `datasets/<role>/<roi>/<ok|ng>/` (PNG + metadata JSON).
- **Logs**: Backend/GUI expectations documented in [`LOGGING.md`](../../LOGGING.md).

## Model & Dataset Update Playbook

1. **Place artefacts** under `backend/models/<role>/<roi>/` using the same naming convention (`memory.npz`, `index.faiss`, `calib.json`).
2. **Run smoke tests**:
   ```bash
   curl http://127.0.0.1:8000/health
   curl -X POST http://127.0.0.1:8000/infer -F role_id=<role> -F roi_id=<roi> -F mm_per_px=<value> -F image=@<sample>
   ```
   Capture the JSON responses as evidence.
3. **Document metrics**: record `score`, `threshold`, `token_shape`, and any calibration parameters in [`latest_updates.md`](latest_updates.md).
4. **Update dataset manifests** if new PNG/JSON pairs are added (counts, timestamps, mm/px values).

## Logging & Observability Coordination

Expectations are centralised in [`LOGGING.md`](../../LOGGING.md):

- Backend logs `/fit_ok`, `/calibrate_ng`, `/infer` with correlation IDs and latencies.
- GUI logs dataset operations and backend responses without PII.
- Weekly log rotation (logrotate or Task Scheduler) with at least eight compressed backups.
- Optional metrics (latency, OK/NG counts) reported to stakeholders.

## Release Cadence

- **Coordinated releases**: Backend and GUI should be released together when contracts change.
- **Emergency fixes**: Document hotfixes (who/when/why) in [`latest_updates.md`](latest_updates.md).
- **Model refreshes**: Always run smoke tests and capture evidence before swapping artefacts.

### Release Readiness Checklist

- [ ] Smoke tests completed (`/health`, `/fit_ok`, `/infer`).
- [ ] `memory.npz` + `calib.json` present for all active roles/ROIs.
- [ ] GUI `appsettings.json` updated con la URL correcta del backend.
- [ ] Logs revisados durante la primera hora tras despliegue.
- [ ] Entrada en [`latest_updates.md`](latest_updates.md) con resultados y responsables.

## Maintaining this Folder

- Scope limited to MCP coordination topics.
- Add a dated entry to [`latest_updates.md`](latest_updates.md) for every meaningful change.
- Cross-link new MCP documents from here and ensure README references remain valid.
