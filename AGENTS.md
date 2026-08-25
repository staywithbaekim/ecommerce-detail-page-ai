# Repository Guidance

## Purpose

Build the Windows desktop application defined by the signed PRD. Photoshop is a controlled rendering environment; the desktop application owns the user workflow, AI analysis, rules, task state, and recovery.

## Source Of Truth

Read these sources in order before changing behavior:

1. `项目总览.md` defines the active project flow and reading entry points.
2. `产品需求文档-电商详情页智能生产系统-PRD-1.2.md` is the signed product baseline and the only active product requirement source.
3. Approved records under `docs/架构决策/` define technical decisions without lowering PRD goals.
4. The active brief under `docs/任务/` defines the current bounded outcome.
5. `docs/验收矩阵.md` defines traceable verification.
6. `docs/当前项目状态.md` records the latest handoff and blockers.

Historical reports under `docs/验证/` and archived transport packages are not default reading inputs. Read them only when the active task explicitly needs their evidence or a technical contract.

Chat conclusions are not durable until they are reflected in the appropriate repository document. A newer approved change must update the affected baseline or decision record instead of silently contradicting it.

## Non-Negotiable Rules

- Never modify or overwrite user source PSDs, source photos, market size images, or manual reference outputs.
- Perform Photoshop experiments on explicit copies in a controlled temporary or output directory.
- Do not treat every smart object as a replaceable product slot. Use calibrated bindings and structural evidence.
- Do not generate, redraw, or invent product imagery.
- Do not guess missing size-table values or missing required product assets.
- Keep AI and product rules outside the Photoshop execution adapter.
- Preserve a renderer-neutral `RenderPlan` boundary so Photoshop UXP, JSX, or a future renderer can consume the same plan.
- The existing external Demo is evidence and an operation reference only. Do not make the new application depend on its fixed templates, fixed page roles, or source tree.
- UXP v5 is the approved V1 Photoshop execution adapter; keep its validated safety gates and renderer-neutral `RenderPlan` boundary.
- Do not add account systems, payments, cloud template sync, platform upload, or other PRD exclusions to V1.
- Never commit API keys, credentials, customer data, generated PSD/JPG output, caches, or temporary render files.
- Preserve unrelated user changes and untracked files.

## Task Workflow

Before implementation:

1. Read this file, the signed PRD, `docs/当前项目状态.md`, the active task brief, and relevant ADRs.
2. Restate the task goal, constraints, non-goals, and completion criteria in the working plan.
3. Inspect existing files and Git state before editing.

Before handoff:

1. Run verification proportional to the change and record commands and results.
2. Review the diff for scope drift and source-file safety.
3. Update the acceptance matrix when evidence changes a row.
4. Update the active task brief and `docs/当前项目状态.md` with results, blockers, and the next concrete action.
5. Record a new ADR when an implementation choice materially affects architecture, compatibility, performance, or delivery.

## Build And Test Commands

No application scaffold or canonical build command exists yet. The active technical-validation task must document reproducible setup, benchmark, and test commands as they are introduced. Do not invent commands in this file before they work in the repository.

## Completion Standard

A task is complete only when its requested artifact exists, relevant verification has run, evidence is recorded, and the next task can continue from repository files without needing the previous chat transcript.
