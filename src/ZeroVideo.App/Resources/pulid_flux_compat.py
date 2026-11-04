"""ZeroVideo compatibility repair for pinned lldacing PuLID-FLUX nodes.

Current ComfyUI Flux.forward_orig accepts 'timestep_zero_index' and provides
block transformer_options in input_args rather than extra_options. The pinned
PuLID extension monkey-patches forward_orig with an old signature, causing:
TypeError: pulid_forward_orig() got an unexpected keyword argument
'timestep_zero_index'.

For the NEW ComfyUI API, retain the native forward_orig implementation and use
PuLID's existing outer sampler wrapper that applies patch callbacks without
replacing the whole FLUX forward implementation. Let the block hooks read
transformer_options from either API layout. Only exact known source anchors
are changed. Preserve vendor backups; no model data or other nodes are touched.
"""
import argparse
import os
from pathlib import Path
import shutil
import sys
import tempfile


OLD_WRAPPER = (
    "model.add_wrapper_with_key(comfy.patcher_extension.WrappersMP.OUTER_SAMPLE,"
    " wrappers_name, pulid_outer_sample_wrappers_with_override)"
)
NEW_WRAPPER = (
    "model.add_wrapper_with_key(comfy.patcher_extension.WrappersMP.OUTER_SAMPLE,"
    " wrappers_name, pulid_outer_sample_wrappers)"
)
OLD_OPTIONS = 'transformer_options = extra_options["transformer_options"]'
NEW_OPTIONS = (
    'transformer_options = (extra_options.get("transformer_options") '
    'or input_args.get("transformer_options") or {})'
)


class CompatibilityError(RuntimeError):
    pass


def checked_replacement(contents, old, new, count, label):
    originals = contents.count(old)
    updated = contents.count(new)
    if originals == count and updated == 0:
        return contents.replace(old, new), True
    if originals == 0 and updated == count:
        return contents, False
    raise CompatibilityError(
        f"{label} differs from pinned PuLID-FLUX source: "
        f"old={originals}, patched={updated}, expected={count}. "
        "Refusing to modify an unrecognized custom node."
    )


def copy_with_backup(path, original, updated):
    if original == updated:
        return False
    backup = path.with_name(path.name + ".zerovideo-before-flux-compat.bak")
    if not backup.exists():
        # Preserve exactly the original source bytes in a uniquely
        # namespaced backup, never edit unrelated ComfyUI folders.
        shutil.copy2(path, backup)
    temp_name = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", newline="",
            dir=str(path.parent), prefix=path.name + ".zerovideo-",
            suffix=".tmp", delete=False
        ) as file:
            temp_name = file.name
            file.write(updated)
        os.replace(temp_name, path)
        return True
    finally:
        if temp_name and os.path.isfile(temp_name):
            os.unlink(temp_name)


def repair(plugin_dir, flux_model_path, verify_only=False):
    flux_model = Path(flux_model_path)
    if not flux_model.is_file():
        raise CompatibilityError("Bundled ComfyUI FLUX model.py was not found: " +
                                 str(flux_model))
    native_source = flux_model.read_text(encoding="utf-8")
    # Older ComfyUI did not have this extra argument. Leave it unchanged.
    if "timestep_zero_index" not in native_source:
        return "Older ComfyUI FLUX API detected; pinned legacy PuLID wrapper unchanged."

    plugin = Path(plugin_dir)
    main_file = plugin / "pulidflux.py"
    hook_file = plugin / "PulidFluxHook.py"
    if not main_file.is_file() or not hook_file.is_file():
        raise CompatibilityError(
            "PuLID-FLUX extension is incomplete. Click Install & Set Up "
            "PuLID-FLUX to repair it."
        )
    main_original = main_file.read_text(encoding="utf-8")
    hook_original = hook_file.read_text(encoding="utf-8")
    if ("class ApplyPulidFlux" not in main_original
            or "def pulid_forward_orig(" not in hook_original
            or "class DitSingleBlockReplace" not in hook_original):
        raise CompatibilityError(
            "Unexpected PuLID extension structure; no files changed."
        )

    # Validate BOTH files fully before writing EITHER file.
    main_updated, change_main = checked_replacement(
        main_original, OLD_WRAPPER, NEW_WRAPPER, 1,
        "PuLID outer sampler wrapper"
    )
    hook_updated, change_hook = checked_replacement(
        hook_original, OLD_OPTIONS, NEW_OPTIONS, 3,
        "PuLID transformer_options hook"
    )
    if verify_only and (change_main or change_hook):
        raise CompatibilityError(
            "Installed PuLID extension is incompatible with this ComfyUI "
            "(old forward_orig wrapper). Click Identity Studio > "
            "Install & Set Up PuLID-FLUX again, restart ComfyUI, then test."
        )
    if not verify_only:
        copy_with_backup(main_file, main_original, main_updated)
        copy_with_backup(hook_file, hook_original, hook_updated)
    return (
        "PuLID native ComfyUI FLUX forward compatibility "
        + ("verified (already patched)." if not (change_main or change_hook)
           else ("verified (patch needed)." if verify_only
                 else "repaired with vendor source backups."))
        + " Restart ComfyUI to load the updated nodes."
    )


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--extension", required=True)
    parser.add_argument("--flux-model", required=True)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args(argv)
    try:
        print(repair(args.extension, args.flux_model, args.verify_only),
              flush=True)
    except (OSError, UnicodeError, CompatibilityError) as exc:
        print("PuLID FLUX compatibility repair failed: " + str(exc),
              file=sys.stderr, flush=True)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
