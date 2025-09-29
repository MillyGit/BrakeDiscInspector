# MCP Latest Updates

This log records notable Maintenance & Communication Plan (MCP) events for BrakeDiscInspector. Add the most recent entries to the top of the document and include:

- **Date** (ISO `YYYY-MM-DD`)
- **Owner(s)** responsible for the update
- **Summary** of the change and links to detailed documents or pull requests
- **Next Steps / Follow-ups** when applicable

## 2024-06-05 — PatchCore Documentation Refresh

- **Owners:** MCP Maintainer, Backend Lead, GUI Lead
- **Summary:** Updated all repository markdown files to align with the FastAPI + PatchCore backend (DINOv2 extractor, `/fit_ok`/`/calibrate_ng`/`/infer` contract) and the dataset-driven GUI workflow. Simplified the MCP scope to focus on the new artefact layout (`models/<role>/<roi>/`).
- **Evidence:** See refreshed guides (`README.md`, `ARCHITECTURE.md`, `API_REFERENCE.md`, `DATA_FORMATS.md`, `DEV_GUIDE.md`, `DEPLOYMENT.md`, `LOGGING.md`, `ROI_AND_MATCHING_SPEC.md`).
- **Next Steps:** Monitor first production deployment under the new backend. Capture latency metrics and calibration outcomes in the next MCP entry.

## 2024-05-28 — Initial MCP Publication

- **Owners:** Repository maintainers
- **Summary:** Created the MCP documentation hub (`docs/mcp/overview.md`) and the ongoing change log (`docs/mcp/latest_updates.md`). Linked the new hub from the main README for discoverability.
- **Next Steps:** Populate this log whenever datasets, models, or architecture contracts change.
