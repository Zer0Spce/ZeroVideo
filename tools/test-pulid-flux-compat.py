"""Offline regression checks for ZeroVideo PuLID/ComfyUI forward compatibility.

No real ComfyUI install, GPU or model weights are needed.
"""
import importlib.util
from pathlib import Path
import tempfile
import unittest

HERE = Path(__file__).resolve().parent.parent
SCRIPT = HERE / "src" / "ZeroVideo.App" / "Resources" / "pulid_flux_compat.py"
spec = importlib.util.spec_from_file_location("pulid_flux_compat", SCRIPT)
compat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compat)


class PuLidFluxCompatibilityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.plugin = root / "ComfyUI_PuLID_Flux_ll"
        self.plugin.mkdir()
        self.flux = root / "model.py"
        self.flux.write_text(
            "class Flux:\n    def forward_orig(self, timestep_zero_index=None): pass\n",
            encoding="utf-8"
        )
        self.main = self.plugin / "pulidflux.py"
        self.hooks = self.plugin / "PulidFluxHook.py"
        self.main.write_text(
            "class ApplyPulidFlux:\n    pass\n" +
            "    " + compat.OLD_WRAPPER + "\n",
            encoding="utf-8"
        )
        self.hooks.write_text(
            "def pulid_forward_orig():\n    pass\n"
            "class DitSingleBlockReplace:\n    pass\n" +
            ("    " + compat.OLD_OPTIONS + "\n") * 3,
            encoding="utf-8"
        )

    def test_patches_only_known_wrapper_and_hook_sites(self):
        old_main = self.main.read_text()
        old_hooks = self.hooks.read_text()
        out = compat.repair(self.plugin, self.flux)
        self.assertIn("repaired", out)
        self.assertIn(compat.NEW_WRAPPER, self.main.read_text())
        self.assertEqual(self.main.read_text().count(compat.OLD_WRAPPER), 0)
        self.assertEqual(self.hooks.read_text().count(compat.NEW_OPTIONS), 3)
        self.assertEqual(self.hooks.read_text().count(compat.OLD_OPTIONS), 0)
        self.assertEqual(
            self.main.with_name(
                self.main.name + ".zerovideo-before-flux-compat.bak"
            ).read_text(), old_main
        )
        self.assertEqual(
            self.hooks.with_name(
                self.hooks.name + ".zerovideo-before-flux-compat.bak"
            ).read_text(), old_hooks
        )
        self.assertIn("verified", compat.repair(
            self.plugin, self.flux, verify_only=True
        ))

    def test_recover_partially_patched_hook_assignments(self):
        # An interrupted previous repair may leave the three hook callbacks
        # in a mixed old/new state. Repair must be resumable rather than
        # rejecting the folder as damaged.
        old = self.hooks.read_text()
        partial = old.replace(
            compat.OLD_OPTIONS, compat.NEW_OPTIONS, 1
        )
        self.hooks.write_text(partial, encoding="utf-8")
        compat.repair(self.plugin, self.flux)
        self.assertEqual(self.hooks.read_text().count(compat.NEW_OPTIONS), 3)
        self.assertEqual(self.hooks.read_text().count(compat.OLD_OPTIONS), 0)

    def test_recover_when_main_already_patched(self):
        self.main.write_text(
            self.main.read_text().replace(
                compat.OLD_WRAPPER, compat.NEW_WRAPPER
            ), encoding="utf-8"
        )
        compat.repair(self.plugin, self.flux)
        self.assertEqual(self.hooks.read_text().count(compat.NEW_OPTIONS), 3)
        self.assertEqual(self.main.read_text().count(compat.NEW_WRAPPER), 1)

    def test_unknown_hook_count_reports_actionable_counts(self):
        self.hooks.write_text(
            self.hooks.read_text().replace(
                compat.OLD_OPTIONS, "", 1
            ), encoding="utf-8"
        )
        with self.assertRaisesRegex(
            compat.CompatibilityError,
            "expected 3 transformer_options hook assignments"
        ):
            compat.repair(self.plugin, self.flux)

    def test_idempotent_does_not_overwrite_backup(self):
        compat.repair(self.plugin, self.flux)
        original_backup = self.main.with_name(
            self.main.name + ".zerovideo-before-flux-compat.bak"
        ).read_bytes()
        self.assertIn("already patched", compat.repair(self.plugin, self.flux))
        self.assertEqual(original_backup, self.main.with_name(
            self.main.name + ".zerovideo-before-flux-compat.bak"
        ).read_bytes())

    def test_verify_only_fails_before_changes(self):
        initial = self.main.read_text()
        with self.assertRaisesRegex(compat.CompatibilityError, "Install"):
            compat.repair(self.plugin, self.flux, verify_only=True)
        self.assertEqual(initial, self.main.read_text())

    def test_unknown_vendor_code_never_modified(self):
        self.main.write_text(
            "class ApplyPulidFlux:\n"
            "    pass\n"
            "    unknown_outer_wrapper_call()\n", encoding="utf-8"
        )
        original = self.main.read_text()
        with self.assertRaises(compat.CompatibilityError):
            compat.repair(self.plugin, self.flux)
        self.assertEqual(original, self.main.read_text())
        self.assertEqual(
            self.hooks.read_text().count(compat.OLD_OPTIONS), 3
        )

    def test_legacy_comfy_does_not_require_patch(self):
        self.flux.write_text(
            "class Flux:\n    def forward_orig(self): pass\n",
            encoding="utf-8"
        )
        old = self.main.read_text()
        self.assertIn("Older ComfyUI", compat.repair(self.plugin, self.flux))
        self.assertEqual(old, self.main.read_text())


if __name__ == "__main__":
    unittest.main()
