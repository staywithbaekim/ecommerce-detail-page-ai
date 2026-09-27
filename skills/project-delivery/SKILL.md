---
name: project-delivery
description: Plan delivery stages, assess acceptance evidence, or review project-specific agent rules for this ecommerce detail-page desktop repository (ARCH, SIZE, RENDER). Use when deciding stage dependencies, handoffs, or completion gates; ordinary implementation and generic Photoshop work do not need this skill.
---

# Project delivery

Use this skill only for this repository’s stage routing, handoff planning, acceptance evidence, or project-specific agent and skill review.

1. Use the repository `AGENTS.md`; read it only if it is not already in the current context.
2. Choose the smallest route:
   - Stage or handoff question: read the relevant sections of `项目总览.md` and `docs/当前项目状态.md`.
   - Stage implementation prerequisites: read the active `docs/任务/` brief and only the ADRs it cites or the change requires.
   - Validation or handoff: read the relevant rows of `docs/验收矩阵.md` and the cited evidence report.
   - Photoshop or RENDER stage planning: read `docs/任务/RENDER-01正式Photoshop-UXP渲染适配器纵向实现任务书.md` and `docs/任务/RENDER-01-SIZE-01渲染交接合同.md`; read the computer-use skill only when native UI control is actually needed.
   - Agent or skill review: inspect `AGENTS.md`, the relevant skill descriptions, and the exact referenced skill bodies. Do not read unrelated skills.
3. Keep the renderer-neutral `RenderPlan` boundary and the source-media safety rules. Never substitute a validation fixture for the formal Photoshop adapter or real output evidence.
4. When reporting product acceptance, distinguish code implemented, offline/fixture evidence, and real environment acceptance. A passing test count does not close a real Photoshop or product-owner gate. A rules-only review needs its own document and skill validation results.
5. Apply the authorization rules in `AGENTS.md`; this skill adds no approval gate.
6. Finish with the next concrete gate, the user’s required input (if any), and the evidence that will close it.
