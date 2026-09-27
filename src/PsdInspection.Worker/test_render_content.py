import copy
import unittest
from types import SimpleNamespace

from psd_tools.psd import descriptor as d
from psd_tools.psd import engine_data as e
from psd_tools.psd import adjustments as a
from psd_tools.constants import EffectOSType, Tag
from psd_tools.psd.color import Color
from psd_tools.psd.effects_layer import BevelInfo, EffectsLayer
from psd_tools.psd.layer_and_mask import MaskData
from psd_tools.terminology import Unit

from worker import adjustment_hash, empty_mask_hash, effect_descriptor_value, effects_hash, text_style_hash, shape_content_hash


def effects():
    gradient = d.Descriptor(classID=b"Grdn", items={
        b"Nm  ": d.String("$$$/DefaultGradient/ForegroundToTransparent=Foreground to Transparent\0"),
        b"Clrs": d.List([d.Double(120.0)]),
    })
    return d.DescriptorBlock2(items={b"masterFXSwitch": d.Bool(True),
        b"GrFl": d.Descriptor(classID=b"GrFl", items={
            b"enab": d.Bool(True), b"Grad": gradient,
            b"Opct": d.UnitFloat(unit=Unit.Percent, value=100.0),
        })})


class RenderEffectEvidenceTests(unittest.TestCase):
    def test_disabled_linear_contour_save_alias_preserves_curve(self):
        before = outer_glow()
        after = copy.deepcopy(before)
        after[b"TrnS"][b"Nm  "].value = "线性\0"
        self.assertEqual(effect_descriptor_value(before), effect_descriptor_value(after))
        self.assertEqual(before[b"TrnS"][b"Nm  "].value, "默认\0")

    def test_contour_alias_is_not_applied_outside_observed_case(self):
        for mutation in ("enabled", "present", "effect", "curve", "extra", "point_extra", "unit", "label"):
            with self.subTest(mutation=mutation):
                before = outer_glow()
                if mutation == "enabled": before[b"enab"].value = True
                if mutation == "present": before[b"present"].value = True
                if mutation == "effect": before.classID = b"IrGl"
                if mutation == "curve": before[b"TrnS"][b"Crv "][1][b"Vrtc"].value = 254.0
                if mutation == "extra": before[b"TrnS"][b"future"] = d.Bool(True)
                if mutation == "point_extra": before[b"TrnS"][b"Crv "][0][b"future"] = d.Bool(True)
                if mutation == "unit": before[b"TrnS"][b"Crv "][0][b"Hrzn"] = d.Integer(0)
                if mutation == "label": before[b"TrnS"][b"Nm  "].value = "Custom"
                after = copy.deepcopy(before)
                after[b"TrnS"][b"Nm  "].value = "线性\0"
                self.assertNotEqual(effect_descriptor_value(before), effect_descriptor_value(after))

    def test_disabled_half_opacity_legacy_cache_matches_photoshop_save(self):
        before = bevel_layer()
        after = copy.deepcopy(before)
        after.tagged_blocks[Tag.EFFECTS_LAYER][EffectOSType.BEVEL].highlight_opacity = 128
        after.tagged_blocks[Tag.EFFECTS_LAYER][EffectOSType.BEVEL].shadow_opacity = 128
        self.assertEqual(effects_hash(before), effects_hash(after))
        self.assertEqual(before.tagged_blocks[Tag.EFFECTS_LAYER][EffectOSType.BEVEL].highlight_opacity, 127)

    def test_legacy_cache_alias_requires_disabled_typed_modern_half_opacity(self):
        for mutation in ("legacy_only", "legacy_enabled", "version", "enabled", "present", "missing", "percent", "unit", "type", "opacity", "conflicting_block"):
            with self.subTest(mutation=mutation):
                before = bevel_layer()
                legacy = before.tagged_blocks[Tag.EFFECTS_LAYER][EffectOSType.BEVEL]
                modern = before.tagged_blocks[Tag.OBJECT_BASED_EFFECTS_LAYER_INFO][b"ebbl"]
                if mutation == "legacy_only": del before.tagged_blocks[Tag.OBJECT_BASED_EFFECTS_LAYER_INFO]
                if mutation == "legacy_enabled": legacy.enabled = 1
                if mutation == "version": legacy.version = 0
                if mutation == "enabled": modern[b"enab"].value = True
                if mutation == "present": modern[b"present"].value = True
                if mutation == "missing": del modern[b"present"]
                if mutation == "percent": modern[b"hglO"].value = 49.0
                if mutation == "unit": modern[b"hglO"].unit = Unit.Pixels
                if mutation == "type": modern[b"hglO"] = d.Double(50.0)
                if mutation == "opacity": legacy.shadow_opacity = 126
                if mutation == "conflicting_block": before.tagged_blocks[Tag.OBJECT_BASED_EFFECTS_LAYER_INFO_V0] = d.DescriptorBlock()
                after = copy.deepcopy(before)
                after.tagged_blocks[Tag.EFFECTS_LAYER][EffectOSType.BEVEL].highlight_opacity = 128
                self.assertNotEqual(effects_hash(before), effects_hash(after))

    def test_actual_bevel_parameters_and_real_colors_remain_evidence(self):
        for mutation in ("modern_color", "modern_percent", "enabled", "blur", "base_color", "real_highlight", "real_shadow", "opacity"):
            with self.subTest(mutation=mutation):
                before = bevel_layer()
                after = copy.deepcopy(before)
                legacy = after.tagged_blocks[Tag.EFFECTS_LAYER][EffectOSType.BEVEL]
                modern = after.tagged_blocks[Tag.OBJECT_BASED_EFFECTS_LAYER_INFO][b"ebbl"]
                if mutation == "modern_color": modern[b"hglC"] = d.Double(1.0)
                if mutation == "modern_percent": modern[b"hglO"].value = 49.0
                if mutation == "enabled": modern[b"enab"].value = True
                if mutation == "blur": legacy.blur = 1
                if mutation == "base_color": legacy.highlight_color.values[0] = 100
                if mutation == "real_highlight": legacy.real_highlight_color.values[0] = 100
                if mutation == "real_shadow": legacy.real_shadow_color.values[0] = 100
                if mutation == "opacity": legacy.highlight_opacity = 126
                self.assertNotEqual(effects_hash(before), effects_hash(after))

    def test_photoshop_save_defaults_and_observed_localized_label_match(self):
        before = effects()
        after = copy.deepcopy(before)
        after[b"numModifyingFX"] = d.Integer(1)
        after[b"GrFl"][b"gs99"] = d.Enumerated(typeID=b"gradientInterpolationMethodType", enum=b"Gcls")
        after[b"GrFl"][b"Grad"][b"Nm  "] = d.String("前景色到透明渐变\0")
        self.assertEqual(effect_descriptor_value(before, True), effect_descriptor_value(after, True))
        self.assertNotIn(b"gs99", before[b"GrFl"])

    def test_actual_effect_changes_remain_distinguishable(self):
        for mutation in ("opacity", "color", "interpolation", "enabled", "unknown", "label", "unit"):
            with self.subTest(mutation=mutation):
                before = effects()
                after = copy.deepcopy(before)
                gradient = after[b"GrFl"]
                if mutation == "opacity": gradient[b"Opct"].value = 99.0
                if mutation == "color": gradient[b"Grad"][b"Clrs"][0].value = 121.0
                if mutation == "interpolation":
                    gradient[b"gs99"] = d.Enumerated(typeID=b"gradientInterpolationMethodType", enum=b"Gper")
                if mutation == "enabled": gradient[b"enab"].value = False
                if mutation == "unknown": gradient[b"futureParameter"] = d.Integer(1)
                if mutation == "label": gradient[b"Grad"][b"Nm  "] = d.String("User gradient")
                if mutation == "unit": gradient[b"Opct"].unit = Unit.Pixels
                self.assertNotEqual(effect_descriptor_value(before, True), effect_descriptor_value(after, True))

    def test_unknown_types_and_inconsistent_count_fail_closed(self):
        for value in (d.Integer(2), d.String("1")):
            block = effects()
            block[b"numModifyingFX"] = value
            with self.assertRaises(ValueError): effect_descriptor_value(block, True)
        with self.assertRaises(ValueError): effect_descriptor_value(object())


def outer_glow():
    return d.Descriptor(classID=b"OrGl", items={b"enab": d.Bool(False), b"present": d.Bool(False),
        b"TrnS": d.Descriptor(classID=b"ShpC", items={b"Nm  ": d.String("默认\0"),
            b"Crv ": d.List([d.Descriptor(classID=b"CrPt", items={
                b"Hrzn": d.Double(value), b"Vrtc": d.Double(value)}) for value in (0.0, 255.0)])})})


def bevel_layer():
    class Blocks(dict):
        get_data = dict.__getitem__
    return SimpleNamespace(tagged_blocks=Blocks({
        Tag.EFFECTS_LAYER: EffectsLayer(items={EffectOSType.BEVEL: BevelInfo(version=2,
            highlight_opacity=127, shadow_opacity=127, real_highlight_color=Color(), real_shadow_color=Color())}),
        Tag.OBJECT_BASED_EFFECTS_LAYER_INFO: d.DescriptorBlock2(items={b"ebbl": d.Descriptor(classID=b"ebbl", items={
            b"enab": d.Bool(False), b"present": d.Bool(False),
            b"hglO": d.UnitFloat(unit=Unit.Percent, value=50.0), b"sdwO": d.UnitFloat(unit=Unit.Percent, value=50.0)})})}))


def text_layer():
    def dictionary(items, kind=e.Dict):
        return kind({e.Property(key): value for key, value in items.items()})
    engine = dictionary({"EngineDict": dictionary({"StyleRun": dictionary({"FontSize": e.Float(12.0)}),
        "ParagraphRun": dictionary({"Justification": e.Integer(0)})}),
        "ResourceDict": dictionary({"FontSet": e.List([e.String("Synthetic Font")])}),
        "DocumentResources": dictionary({})}, e.EngineData)
    descriptor = d.DescriptorBlock(items={b"Txt ": d.String("sample"),
        b"EngineData": d.RawData(engine.tobytes()),
        b"Ornt": d.Enumerated(typeID=b"Ornt", enum=b"Hrzn"), b"AntA": d.Integer(1),
        b"bounds": d.Descriptor(items={b"Rght": d.Double(100.0)})})
    data = SimpleNamespace(version=1, text_version=50, warp_version=1,
        transform=(1.0, 0.0, 0.0, 1.0, 10.0, 20.0), left=0, top=0, right=100, bottom=30,
        warp=d.DescriptorBlock(items={b"warpValue": d.Double(0.0)}), text_data=descriptor)
    return SimpleNamespace(_data=data, _engine_data=engine)


class RenderTextStyleTests(unittest.TestCase):
    def test_save_default_and_computed_bounds_do_not_change_editable_style(self):
        before = text_layer()
        after = copy.deepcopy(before)
        after._data.text_data[b"TxMg"] = d.Enumerated(typeID=b"TxMg", enum=b"TxNM")
        after._data.text_data[b"bounds"][b"Rght"].value = 101.0
        self.assertEqual(text_style_hash(before), text_style_hash(after))

    def test_font_size_paragraph_transform_warp_and_text_changes_are_detected(self):
        for mutation in ("font", "size", "paragraph", "transform", "warp", "text", "mixing"):
            with self.subTest(mutation=mutation):
                before = text_layer()
                after = copy.deepcopy(before)
                if mutation == "font": after._engine_data["ResourceDict"]["FontSet"][0].value = "Changed Font"
                if mutation == "size": after._engine_data["EngineDict"]["StyleRun"]["FontSize"].value = 14.0
                if mutation == "paragraph": after._engine_data["EngineDict"]["ParagraphRun"]["Justification"].value = 1
                if mutation == "transform": after._data.transform = (2.0, 0.0, 0.0, 1.0, 10.0, 20.0)
                if mutation == "warp": after._data.warp[b"warpValue"].value = 1.0
                if mutation == "text": after._data.text_data[b"Txt "].value = "changed"
                if mutation == "mixing": after._data.text_data[b"TxMg"] = d.Enumerated(typeID=b"TxMg", enum=b"Other")
                self.assertNotEqual(text_style_hash(before), text_style_hash(after))

    def test_unknown_fields_or_missing_engine_fail_closed(self):
        layer = text_layer()
        layer._data.text_data[b"futureStyle"] = d.Integer(1)
        with self.assertRaises(ValueError): text_style_hash(layer)
        layer = text_layer()
        layer._engine_data = e.EngineData({})
        with self.assertRaises(ValueError): text_style_hash(layer)


class RenderAdjustmentTests(unittest.TestCase):
    def test_fixed_shape_covers_all_fill_vector_stroke_and_unknown_bytes(self):
        blocks = {Tag.SOLID_COLOR_SHEET_SETTING: d.DescriptorBlock(items={b"color": d.Double(1.0)}),
                  Tag.VECTOR_MASK_SETTING1: d.RawData(b"vector"),
                  Tag.VECTOR_STROKE_DATA: d.RawData(b"stroke"), b"new!": d.RawData(b"unknown")}
        layer = SimpleNamespace(kind="shape", tagged_blocks=blocks, has_vector_mask=lambda: True)
        before = shape_content_hash(layer)
        self.assertEqual(before, shape_content_hash(copy.deepcopy(layer)))
        for tag in blocks:
            changed = copy.deepcopy(layer)
            changed.tagged_blocks[tag] = d.RawData(b"changed")
            self.assertNotEqual(before, shape_content_hash(changed))
        changed = copy.deepcopy(layer)
        del changed.tagged_blocks[b"new!"]
        self.assertNotEqual(before, shape_content_hash(changed))
        for changed in (SimpleNamespace(kind="shape", tagged_blocks={}, has_vector_mask=lambda: True),
                        SimpleNamespace(kind="shape", tagged_blocks=blocks, has_vector_mask=lambda: False)):
            with self.assertRaises(ValueError): shape_content_hash(changed)

    def test_empty_mask_background_flags_and_parameters_are_evidence(self):
        data = MaskData(background_color=255)
        mask = SimpleNamespace(width=0, height=0, data=data)
        before = empty_mask_hash(mask)
        data.background_color = 0
        self.assertNotEqual(before, empty_mask_hash(mask))
        data.background_color = 255
        data.flags.mask_disabled = True
        self.assertNotEqual(before, empty_mask_hash(mask))
        mask.width = 1
        with self.assertRaises(ValueError): empty_mask_hash(mask)

    def test_curve_points_channels_and_exposure_are_preserved(self):
        for kind, tag, block, field, value in (
            ("curves", Tag.CURVES, a.Curves(False, 4, 1, [[(0, 0), (255, 255)]]), "data", [[(0, 0), (200, 255)]]),
            ("curves", Tag.CURVES, a.Curves(False, 4, 1, [[(0, 0), (255, 255)]]), "count_map", 2),
            ("exposure", Tag.EXPOSURE, a.Exposure(1, 0.0, 0.0, 1.0), "exposure", 1.0),
            ("exposure", Tag.EXPOSURE, a.Exposure(1, 0.0, 0.0, 1.0), "offset", 0.2),
            ("exposure", Tag.EXPOSURE, a.Exposure(1, 0.0, 0.0, 1.0), "gamma", 1.2),
        ):
            with self.subTest(kind=kind, field=field):
                class Blocks(dict):
                    get_data = dict.__getitem__
                layer = SimpleNamespace(kind=kind, tagged_blocks=Blocks({tag: block}))
                before = adjustment_hash(layer)
                self.assertEqual(before, adjustment_hash(copy.deepcopy(layer)))
                setattr(block, field, value)
                self.assertNotEqual(before, adjustment_hash(layer))

    def test_unsupported_or_missing_adjustment_is_rejected(self):
        for kind in ("curves", "exposure", "unknown"):
            with self.assertRaises(ValueError):
                adjustment_hash(SimpleNamespace(kind=kind, tagged_blocks={}))


if __name__ == "__main__":
    unittest.main()
