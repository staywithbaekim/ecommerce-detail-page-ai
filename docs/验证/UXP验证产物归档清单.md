# UXP 验证产物归档清单

## 目的

记录已完成远程传输的 UXP 验证 ZIP，确保删除运输包后仍可追溯验证版本、用途、大小和 SHA-256。ZIP 不属于源码，不作为后续 Agent 的默认阅读入口。

## 处理结论

- 30 个根目录 `uxp-validation-*.zip` 已完成传输和验证，并于 2026-08-25 删除，共 `2,160,714,725` 字节；
- `tmp/` 为可重建临时副本，已于 2026-08-25 清空并删除目录，共删除 452 个文件、`2,024,825,019` 字节；
- 保留的长期资产是 `uxp-validation-plugin/`、`tools/`、`docs/验证/`、`docs/架构决策/` 和 `docs/验收矩阵.md`；
- 用户样本和外部 Demo 不在本次删除范围内。

本次共释放 `4,185,539,744` 字节（约 `3.90 GiB`）。删除后复核：根目录匹配 ZIP 为 0，`tmp/` 不存在，用户样本、外部 Demo、UXP 源码、工具和长期证据均仍存在。ZIP 与临时副本已从本机永久删除；后续复现以仓库中的长期资产和本清单记录为依据。

## ZIP 清单

| 文件 | 字节数 | SHA-256 | 用途/结论 |
| --- | ---: | --- | --- |
| `uxp-validation-g03-fixture-0.2.0.zip` | 35358171 | `F9EB669276E75BCFD58CD250BFB3D9AB8C0315281047B80D14B7419584BE0CED` | G03 单框初版夹具；历史失败前版本 |
| `uxp-validation-g03-fixture-0.2.1.zip` | 35358515 | `926E423624C00CF6B18C263AB1BF6CF68C5B1659A3A16176DCDE1984DB3BCB36` | G03 路径顺序修复夹具 |
| `uxp-validation-g03-fixture-0.2.2.zip` | 35359871 | `7D016C96C0DF63A7A25491A9B54682E5E87AA7CC9A57F1ACA58A75222BE16F2E` | G03 sRGB 尝试夹具；ICC 失败证据 |
| `uxp-validation-g03-fixture-0.2.3.zip` | 35361916 | `204DAFC66594500EBCFAF16EB2B6C867D000FE86A282419CB7D37B3F575F12F7` | G03 最终 ICC 验证夹具 |
| `uxp-validation-probe-0.1.1.zip` | 7070 | `3244521E66F532BDF70D537EB7A12560DC3DC247E47FCA85B7AF63259E5DEE79` | UXP 探针与 bridge |
| `uxp-validation-probe-0.2.0.zip` | 15783 | `F214EFCD4B8839F2AA9302AC81185308A652F065795EC1344BE329EA28FBEB0D` | 单框执行器初版 |
| `uxp-validation-probe-0.2.1.zip` | 16489 | `6222B336E94AFE1A0EC884727C2589CFAF77A5EBB5A2FCCF0241F3D5F825A3AA` | 单框路径顺序修复 |
| `uxp-validation-probe-0.2.2.zip` | 18209 | `20958E4710159F75C4A14481E8D2978D66EE0C3217C96B9954E46DFA46D7928E` | sRGB 请求版本 |
| `uxp-validation-probe-0.2.3.zip` | 26448 | `A76883E842D5DBA02309755BD44D8109B8C09657231ACE04B362A884E20E9AA3` | sRGB ICC 读回/补写版本 |
| `uxp-validation-tv06-multi-frame-0.3.0.zip` | 126297458 | `0ED7F934897A9D5C8561E5A3EAFCD6DC671A6AE2FFD48D2212F39052F697B5F9` | 四框复杂页初版 |
| `uxp-validation-tv06-multi-frame-0.3.0.1.zip` | 126292349 | `1193BF69A7A7AC2BC01866D6B190A854F65F2867B66CBA4583FBEBDE151205F3` | 四框输入检查修正版 |
| `uxp-validation-tv06-plugin-0.3.1.zip` | 24337 | `55908E755B042D587AE01B21287E0A07C3892372DB5FEFEF491DE99BA6C793A1` | 四框错误保真修复 |
| `uxp-validation-tv06-plugin-0.3.2.zip` | 24537 | `331D65EFBAAF469C058B5CA2E882702669F28B04512C22EBF8B21974EFBF5167` | 四框双错误保真 |
| `uxp-validation-tv06-plugin-0.3.3.zip` | 24838 | `83A176BA9817C656343B041A6B70B96E7C953BA2E37098C2ADF32BA075FD1963` | 名称空白兼容 |
| `uxp-validation-tv06-plugin-0.3.4.zip` | 25509 | `E7EE8A6CFC45B87A970A77DEC58796B522B2ACA610B602A698F7411B94CC4F75` | 文档句柄诊断修复 |
| `uxp-validation-tv06-plugin-0.3.5.zip` | 25959 | `5EC2C63B59B6C971CA4B9535A878204E1F5C94DEFB976305BF7ADD2179AC4E60` | 四框最终执行版本 |
| `uxp-validation-tv06-verifier-0.3.5.zip` | 5138 | `76E5A13FB93E696606E5AFEF32F5C1DB80A504C23CE44609FAF7E189B07B0D2F` | 四框回传校验器 |
| `uxp-validation-tv08-editability-0.2.3.zip` | 40174588 | `D31457A9A9E87B73C1907F5E3F123C5319739F48603CFC32141557B2829954A5` | 可编辑 PSD 初版 |
| `uxp-validation-tv08-editability-0.2.3.1.zip` | 40174792 | `B89F40E94C7272D363A6F49803525BCE7AB5A1DAE5A582701843478A4285E377` | 可编辑 PSD 启动修正版 |
| `uxp-validation-tv11-whole-product-0.4.0.zip` | 421445651 | `9022735640C43D5635955C1ECC19B7181E87A1BCC551ACCB84DE5BCA07DAFABA` | 整款六页初版 |
| `uxp-validation-tv11-whole-product-0.4.1.zip` | 421445927 | `C1C1FDC61A8EAADBF7D5272E7D358C477B91EED3FDF0DBCC8532109062F1BA49` | 整款预检版本 |
| `uxp-validation-tv11-whole-product-0.4.2.zip` | 421447539 | `ECC8D64A6648AC5EC2EA94E64EBAF60D465EBC5C3090350A43F64CECF23CEAF1` | 整款剪贴校准版本 |
| `uxp-validation-tv11-whole-product-0.4.6.zip` | 421450518 | `4ED1DB4DE6978BA58E05E63132BB1185CC458B3DAE15031DC1A2DB1C7E0CD04F` | 整款最终通过版本 |
| `uxp-validation-tv11-update-0.4.2.zip` | 45274 | `D828F8D4BD7059E235D3AB1D9CC4DAE22B1857001FF028088EC0DB6397A2C9AA` | 整款增量更新 |
| `uxp-validation-tv11-update-0.4.3.zip` | 45868 | `C08E81BC33734551FF5AFCB7BEA84A58035B779AED9B0A5124527B5AA953B6CE` | 跨 modal 打开修复 |
| `uxp-validation-tv11-update-0.4.4.zip` | 46929 | `497C5964D8FD0D8BD50503CAB360222705CB8F7294D1904CF6DB8E9B085A8B8B` | Action Manager 打开适配 |
| `uxp-validation-tv11-update-0.4.5.zip` | 47921 | `846FC878EDCDC32F7B01BFB4DC7EF44D14F1176B178740F421F40559014E6E85` | 替换栈逐层结构 |
| `uxp-validation-tv11-update-0.4.6.zip` | 48177 | `B066D45012016545FDEDBB768D7FE87FD5D682B53E828C2BDBC1DCF80F256B4E` | 目标名称变化契约 |
| `uxp-validation-tv13-update-0.4.7.zip` | 48831 | `869D447C2C868759B48CC30D00E317B5896FED1CD99BA3E8E49C705DB682EB1C` | TV-13 受控失败版本 |
| `uxp-validation-tv13-update-0.4.7.1.zip` | 50113 | `45E26EAA4AE945798AF810321110D4F084828B6142FA67FD8817E14D0648881C` | TV-13 更新器修正版 |

## 长期复现依据

删除 ZIP 不删除能力。复现依据为：

- UXP 源码：`uxp-validation-plugin/`；
- 验证工具：`tools/`；
- RenderPlan 样例：`uxp-validation-plugin/render-plans/`；
- 技术证据：`docs/验证/`；
- 架构约束：`docs/架构决策/`；
- 状态和验收：`docs/当前项目状态.md`、`docs/验收矩阵.md`。
