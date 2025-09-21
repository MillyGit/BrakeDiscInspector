# MCP Overview

The Maintenance & Communication Plan (MCP) keeps all contributors aligned on how the BrakeDiscInspector
platform evolves. It complements the architectural, data-format and API guides by describing **who does what**,
**how updates are coordinated**, and **where canonical artefacts live**.

## Scope

The MCP covers every change that can affect:

- The backend inference service in [`backend/`](../../backend) and its documented interfaces.
- The WPF GUI located in [`gui/`](../../gui).
- Shared datasets, exported models, or configuration thresholds that impact defect detection results.
- Operational tooling (PowerShell scripts, logging, deployment flows) referenced across the repository.

## Roles & Responsibilities

| Role | Primary Responsibilities | Reference Material |
|------|--------------------------|--------------------|
| MCP Maintainer | Curate this documentation, track outstanding action items, and publish updates in [`latest_updates.md`](latest_updates.md). | This folder |
| Backend Lead | Own API/contracts, model management and deployment readiness for the Python service. | [`API_REFERENCE.md`](../../API_REFERENCE.md), [`DEPLOYMENT.md`](../../DEPLOYMENT.md) |
| GUI Lead | Coordinate GUI release cadence and ROI tooling updates. | [`ARCHITECTURE.md`](../../ARCHITECTURE.md), GUI folder |
| Data Steward | Version the training/inference datasets, manage `model/current_model.h5`, and align threshold files. | [`DATA_FORMATS.md`](../../DATA_FORMATS.md), [`ROI_AND_MATCHING_SPEC.md`](../../ROI_AND_MATCHING_SPEC.md) |

## Change Management Workflow

1. **Propose** the change via an issue or pull request. Reference impacted components and link to the most
   relevant spec (architecture, data formats, logging, etc.).
2. **Assess impact** with the appropriate role owner(s). Confirm dataset, API, and GUI compatibility.
3. **Validate** using the documented procedures in [`DEV_GUIDE.md`](../../DEV_GUIDE.md) and
   [`DEPLOYMENT.md`](../../DEPLOYMENT.md). Attach logs or artefacts when the change affects inference results.
4. **Document** the outcome here:
   - Update the pertinent guide (architecture, data format, logging, etc.).
   - Record the summary in [`latest_updates.md`](latest_updates.md) with date, owner, and next steps.
5. **Communicate** the release to the broader team (Slack/email) and include links to the updated markdown files.

## Data & Artefact Registry

- **Models**: `backend/model/current_model.h5` (TensorFlow). Ensure thresholds are stored in `backend/model/threshold.txt`.
- **Sample datasets**: Refer to the locations described in [`DATA_FORMATS.md`](../../DATA_FORMATS.md).
- **Logs**: Locations and rotation policies are maintained in [`LOGGING.md`](../../LOGGING.md).
- **Scripts**: PowerShell utilities live in [`scripts/`](../../scripts) for setup and runtime management.

## Release Cadence

- **GUI + Backend Releases**: Target coordinated releases so ROI contracts and REST endpoints remain synchronized.
- **Emergency Fixes**: Document hotfixes in [`latest_updates.md`](latest_updates.md) with context and mitigation steps.
- **Model Refreshes**: Run evaluation pipelines and capture metrics before swapping `current_model.h5`.

## Maintaining this Folder

- Keep files in `docs/mcp/` scoped to MCP coordination topics.
- Add a dated entry to [`latest_updates.md`](latest_updates.md) for every meaningful change.
- When introducing new MCP subsections, create additional markdown files in this directory and link them from here.

> 📌 **Reminder:** If a change modifies repository structure or contracts, also update cross-references in
> [`README.md`](../../README.md) so downstream contributors can locate the MCP documentation easily.
