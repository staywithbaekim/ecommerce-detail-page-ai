# RenderPlan v1 正反例

## 正例

`render-plan-v1.valid.example.json` 是一个普通套图页的完整正例。它只使用逻辑资源 ID 和内容 hash，不包含本地路径、端口、token 或 Photoshop 命令。

## 反例

以下字段必须被 Schema 或进入 Domain 前的语义校验拒绝：

```json
{
  "schema_version": "render-plan/v1",
  "plan_id": "plan_bad",
  "idempotency_key": "batch_page_attempt_bad",
  "correlation_id": "corr_bad",
  "batch_id": "batch_bad",
  "product_id": "product_bad",
  "template_revision_id": "tplrev_bad",
  "plan_digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "created_at": "2026-08-27T12:00:00Z",
  "required_capabilities": ["smart-object/replace"],
  "resources": [{
    "resource_id": "analysis_copy",
    "role": "analysis_copy",
    "media_type": "image/jpeg",
    "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
  }],
  "document": {
    "template_resource_id": "analysis_copy",
    "page_id": "page_bad",
    "content_type": "ordinary_set",
    "canvas": { "width": 790, "height": 1252, "color_mode": "RGB", "bits": 8 }
  },
  "targets": [],
  "size_table": null,
  "output": {
    "artifact_set_id": "artifact_bad",
    "overwrite_policy": "never",
    "jpg": {
      "enabled": true,
      "file_name": "C:\\outside\\result.jpg",
      "color_profile": "sRGB IEC61966-2.1",
      "rendering_intent": "relative_colorimetric",
      "black_point_compensation": true,
      "dither": true,
      "embed_profile": true
    },
    "editable_psd": { "enabled": false, "file_name": null },
    "atomic_page_commit": true
  },
  "photoshop_command": "batchPlay(...)"
}
```

拒绝原因包括：未允许的资源用途、普通套图页无目标、文件名包含绝对路径，以及未知的 `photoshop_command` 字段。反例仅用于契约测试，不得发送给 Photoshop。
