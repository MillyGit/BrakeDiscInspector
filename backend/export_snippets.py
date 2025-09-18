import os
import sys

# ── Configuration ──────────────────────────────────────────────
# Point this at the folder that actually contains your snippets.
SNIPPETS_DIR    = "/home/millylinux/BrakeDiscDefect_BACKEND"  

# Where to write the merged snippets file
OUTPUT_FILE     = "all_snippets.txt"

# Header before each snippet in the output file
HEADER_TEMPLATE = "\n\n===== Snippet: {rel_path} =====\n\n"

# Extensions to include—feel free to add/remove.
EXTENSIONS      = {".py"}
# ────────────────────────────────────────────────────────────────

def collect_and_write(snippet_root, output_file):
    # 1. Check that snippet_root exists
    if not os.path.isdir(snippet_root):
        print(f"❌ ERROR: snippet directory not found: {snippet_root!r}")
        sys.exit(1)

    found = 0
    with open(output_file, "w", encoding="utf-8") as out_f:
        # 2. Walk the directory
        for dirpath, _, filenames in os.walk(snippet_root):
            for fn in sorted(filenames):
                ext = os.path.splitext(fn)[1].lower()
                if EXTENSIONS and ext not in EXTENSIONS:
                    # skip non-snippet files
                    continue

                abs_path = os.path.join(dirpath, fn)
                rel_path = os.path.relpath(abs_path, snippet_root)

                print(f"➡️  Adding: {rel_path}")
                found += 1

                # Write header + contents
                out_f.write(HEADER_TEMPLATE.format(rel_path=rel_path))
                try:
                    with open(abs_path, "r", encoding="utf-8") as in_f:
                        out_f.write(in_f.read())
                except Exception as e:
                    out_f.write(f"# ERROR reading file: {e}\n")

    # 3. Report back
    abs_out = os.path.abspath(output_file)
    if found:
        print(f"\n✅ {found} snippet(s) written to:\n   {abs_out}")
    else:
        print(f"\n⚠️  No snippets found under {snippet_root!r}.")
        print("   • Check that SNIPPETS_DIR is correct.")
        print("   • Confirm your EXTENSIONS set includes your file types.")
        print("   • Verify you ran this from the folder you think you did.")

if __name__ == "__main__":
    print(f"Starting snippet export…\nWorking dir: {os.getcwd()}\n")
    collect_and_write(SNIPPETS_DIR, OUTPUT_FILE)
