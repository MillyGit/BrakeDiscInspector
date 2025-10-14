# MCP Latest Updates

This log records notable Maintenance & Communication Plan (MCP) events for BrakeDiscInspector. Add the most recent entries to the top of the document and include:

- **Date** (ISO `YYYY-MM-DD`)
- **Owner(s)** responsible for the update
- **Summary** of the change and links to detailed documents or pull requests
- **Next Steps / Follow-ups** when applicable

## Quick index

- [2025-10-07 — Markdown & Memory Restoration Refresh](#2025-10-07--markdown--memory-restoration-refresh)
- [2024-06-12 — Comprehensive Markdown Refresh](#2024-06-12--comprehensive-markdown-refresh)
- [2024-06-05 — PatchCore Documentation Refresh](#2024-06-05--patchcore-documentation-refresh)
- [2024-05-28 — Initial MCP Publication](#2024-05-28--initial-mcp-publication)

## 2025-10-07 — Markdown & Memory Restoration Refresh

- **Owners:** MCP Maintainer, Backend Lead, GUI Lead
- **Summary:** Realigned every markdown in the repo (README, ARCHITECTURE, DEV_GUIDE, API_REFERENCE, DATA_FORMATS, DEPLOYMENT, LOGGING, ROI spec, backend prompt, GUI instructions, MCP docs) with the current FastAPI (`app.py`) and GUI (`BackendClient`, `DatasetManager`) implementations. Added `PROJECT_OVERVIEW.txt` for rapid onboarding/memory restoration and documented storage layout `backend/models/<role>/<roi>/` plus dataset expectations.
- **Evidence:** Updated markdown files in repo root and `docs/mcp/`, backend sources (`backend/app.py`, `backend/infer.py`, `backend/storage.py`), GUI workflow files.
- **Next Steps:** Share the new `PROJECT_OVERVIEW.txt` with onboarding teams; capture feedback on clarity and identify gaps for future automation.

## 2024-06-12 — Comprehensive Markdown Refresh

- **Owners:** MCP Maintainer, Backend Lead, GUI Lead
- **Summary:** Reorganised every Markdown guide with quick indices, environment tables and cross-links to ease onboarding after cache resets. Highlighted environment variables (`DEVICE`, `CORESET_RATE`, `MODELS_DIR`) and clarified dataset manifests for GUI workflows.
- **Evidence:** Updated files — `README.md`, `ARCHITECTURE.md`, `API_REFERENCE.md`, `DATA_FORMATS.md`, `DEV_GUIDE.md`, `DEPLOYMENT.md`, `LOGGING.md`, `ROI_AND_MATCHING_SPEC.md`, `backend/README_backend.md`, `backend/agents_for_backend.md`, `instructions_codex_gui_workflow.md`, `gui/BrakeDiscInspector_GUI_ROI/README.md`, `docs/mcp/overview.md`.
- **Next Steps:** Share the new documentation index with new contributors and capture feedback during the next sync meeting.

## 2024-06-05 — PatchCore Documentation Refresh

- **Owners:** MCP Maintainer, Backend Lead, GUI Lead
- **Summary:** Updated all repository markdown files to align with the FastAPI + PatchCore backend (DINOv2 extractor, `/fit_ok`/`/calibrate_ng`/`/infer` contract) and the dataset-driven GUI workflow. Simplified the MCP scope to focus on the new artefact layout (`models/<role>/<roi>/`).
- **Evidence:** See refreshed guides (`README.md`, `ARCHITECTURE.md`, `API_REFERENCE.md`, `DATA_FORMATS.md`, `DEV_GUIDE.md`, `DEPLOYMENT.md`, `LOGGING.md`, `ROI_AND_MATCHING_SPEC.md`).
- **Next Steps:** Monitor first production deployment under the new backend. Capture latency metrics and calibration outcomes in the next MCP entry.

## 2024-05-28 — Initial MCP Publication

- **Owners:** Repository maintainers
- **Summary:** Created the MCP documentation hub (`docs/mcp/overview.md`) and the ongoing change log (`docs/mcp/latest_updates.md`). Linked the new hub from the main README for discoverability.
- **Next Steps:** Populate this log whenever datasets, models, or architecture contracts change.
