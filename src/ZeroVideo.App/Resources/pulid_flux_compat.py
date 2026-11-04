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
import re
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


# Match only executable wrapper calls/assignment lines; never rewrite
# comments, imported vendor symbols or arbitrary third-party code.
OLD_WRAPPER_LINE = re.compile(
    r'(?m)^([ \t]*)model\.add_wrapper_with_key\(\s*'
    r'comfy\.patcher_extension\.WrappersMP\.OUTER_SAMPLE,\s*'
    r'wrappers_name,\s*pulid_outer_sample_wrappers_with_override\s*\)[ \t]*$'
)
NEW_WRAPPER_LINE = re.compile(
    r'(?m)^([ \t]*)model\.add_wrapper_with_key\(\s*'
    r'comfy\.patcher_extension\.WrappersMP\.OUTER_SAMPLE,\s*'
    r'wrappers_name,\s*pulid_outer_sample_wrappers\s*\)[ \t]*$'
)
OLD_OPTIONS_LINE = re.compile(
    r'''(?m)^([ \t]*)transformer_options\s*=\s*'''
    r'''extra_options\[\s*["']transformer_options["']\s*\][ \t]*$'''
)
NEW_OPTIONS_LINE = re.compile(
    r'''(?m)^([ \t]*)transformer_options\s*=\s*'''
    r'''\(extra_options\.get\(["']transformer_options["']\) '''
    r'''or input_args\.get\(["']transformer_options["']\) or \{\}\)[ \t]*$'''
)


def repair_main_source(content, filename):
    # The upstream file contains TWO outer sampler registrations:
    #   ApplyPulidFlux   -> legacy _with_override (BROKEN on modern ComfyUI)
    #   FixPulidFluxPatch -> already native wrapper (MUST NOT touch).
    # Therefore only count and patch the ApplyPulidFlux class.
    begin = content.find("class ApplyPulidFlux:")
    if begin < 0:
        raise CompatibilityError(
            f"{filename}: ApplyPulidFlux class missing; source not modified."
        )
    end = content.find("class FixPulidFluxPatch:", begin)
    if end < 0:
        # Older fork may omit FixPulidFluxPatch; still isolate the Apply
        # class from unrelated classes beneath it.
        end = content.find("\nclass ", begin + len("class ApplyPulidFlux:"))
    if end < 0:
        end = len(content)
    before, section, after = content[:begin], content[begin:end], content[end:]
    old = list(OLD_WRAPPER_LINE.finditer(section))
    new = list(NEW_WRAPPER_LINE.finditer(section))
    if len(old) + len(new) != 1:
        raise CompatibilityError(
            f"{filename}: ApplyPulidFlux expected exactly one OUTER_SAMPLE "
            f"registration; found old={len(old)}, compatible={len(new)}. "
            "Existing FixPulidFluxPatch registration was intentionally "
            "excluded from this count. No vendor source was overwritten."
        )
    if not old:
        return content, False
    section, count = OLD_WRAPPER_LINE.subn(
        lambda match: match.group(1) + NEW_WRAPPER, section
    )
    assert count == 1
    return before + section + after, True


def repair_hook_source(content, filename):
    old = list(OLD_OPTIONS_LINE.finditer(content))
    new = list(NEW_OPTIONS_LINE.finditer(content))
    # The pinned upstream extension has exactly 3 hooks, but interrupted
    # previous repairs may leave a mixture of old and new hook assignments.
    if len(old) + len(new) != 3:
        raise CompatibilityError(
            f"{filename}: expected 3 transformer_options hook assignments, "
            f"found legacy={len(old)}, compatible={len(new)}. "
            "The installed PuLID extension differs from the pinned source "
            "or a previous edit was incomplete; no unknown code was changed."
        )
    if not old:
        return content, False
    updated, count = OLD_OPTIONS_LINE.subn(
        lambda match: match.group(1) + NEW_OPTIONS, content
    )
    assert count == len(old)
    return updated, True


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
    main_updated, change_main = repair_main_source(
        main_original, str(main_file))
    hook_updated, change_hook = repair_hook_source(
        hook_original, str(hook_file))
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
