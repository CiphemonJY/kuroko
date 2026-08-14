#!/usr/bin/env python3
"""Install the PaperBanana diagram skill into this machine's Claude setup.

PaperBanana (https://github.com/dwzhu-pku/PaperBanana, Apache-2.0) generates
publication-quality diagrams from prose. Its bundled skill is *not*
self-contained: skill/run.py puts the repo root on sys.path and imports
agents/ and utils/, and the Retriever pulls reference images from HuggingFace.
So "installing the skill" means checking out the repo, building it a venv, and
writing a SKILL.md that points Claude at absolute paths.

Run once per machine:

    python tools/install-paperbanana-skill.py

Idempotent: re-running pulls the latest checkout and refreshes the skill file.
Nothing outside the checkout dir and ~/.claude/skills/paperbanana is touched.
"""

import argparse
import os
import platform
import shutil
import subprocess
import sys
from pathlib import Path

REPO_URL = "https://github.com/dwzhu-pku/PaperBanana.git"
SKILL_NAME = "paperbanana"

# The generated SKILL.md. Upstream's own skill/SKILL.md assumes you have already
# cd'd into the repo root and that `python` resolves to the right interpreter;
# neither holds when Claude invokes a skill from an arbitrary cwd. This version
# hard-codes the venv interpreter and the run.py path, and keeps the frontmatter
# to the keys Claude Code reads.
SKILL_TEMPLATE = """---
name: paperbanana
description: >-
  Generate a publication-quality diagram or pipeline figure from prose - a
  method description, an architecture write-up, a README section - using the
  PaperBanana multi-agent pipeline (Retriever, Planner, Stylist, Visualizer,
  Critic). Use when asked for an academic-style figure, an architecture
  diagram, or a paper illustration rendered as an image file. Not for charts
  of numeric data.
---

# PaperBanana

Turns a block of explanatory text plus a caption into a rendered diagram image.
Upstream: https://github.com/dwzhu-pku/PaperBanana (Apache-2.0), from the paper
"PaperBanana: Automating Academic Illustration for AI Scientists"
(arXiv:2601.23265).

## Invocation

Always call it through this exact interpreter and script path:

```bash
{python} {run_py} \\
  --content-file /path/to/method.txt \\
  --caption "Figure 1: overview of the capture pipeline" \\
  --output /path/to/output.png \\
  --num-candidates 3
```

The absolute path of each saved image is printed to stdout, one per line.
With `--num-candidates N` (N > 1) the outputs are `<stem>_0.png` ... `<stem>_{{N-1}}.png`.

Prefer `--content-file` over `--content` for anything longer than a sentence -
the text is prose with quotes and newlines in it, and shell quoting will bite.

## Parameters worth setting

| Flag | Default | Notes |
| --- | --- | --- |
| `--caption` | required | The figure caption / visual intent. Drives the whole plan. |
| `--content` / `--content-file` | one required | The text to visualize. |
| `--output` | `output.png` | Output path. |
| `--num-candidates` | `10` | Parallel candidates. **Override this** - see cost below. |
| `--aspect-ratio` | `21:9` | `21:9`, `16:9`, or `3:2`. Use `16:9` or `3:2` for a README. |
| `--max-critic-rounds` | `3` | Refinement iterations per candidate. |
| `--retrieval-setting` | `auto` | `auto`, `manual`, `random`, `none`. See below. |
| `--exp-mode` | `demo_full` | `demo_full` includes the Stylist; `demo_planner_critic` skips it. |
| `--main-model-name` | from config | Reasoning model for the VLM agents. |
| `--image-gen-model-name` | from config | Image generation model. |

## Cost and runtime - read before running

Each candidate is Retriever + Planner + Stylist + Visualizer + up to 3 Critic
rounds, every one an LLM call, with image generation in the Visualizer and each
Critic round. Upstream quotes 3-10 minutes per candidate and 10-30 minutes for
the default 10.

**Default to `--num-candidates 3` and `--max-critic-rounds 2`** for a first pass,
then re-run wider only if the shape is promising. Tell the user the run is
starting and roughly how long it will take before you launch it.

## The reference dataset

With `--retrieval-setting auto` (the default) the Retriever needs
PaperBananaBench, which `run.py` downloads from HuggingFace on first use into
`{repo}/data/`. That download is a few hundred MB and only happens once.

Pass `--retrieval-setting none` to skip retrieval entirely - no dataset needed,
faster, and a reasonable choice when the target is a software architecture
diagram rather than a paper figure, since the reference corpus is ML-paper
figures.

## Setup state on this machine

- Checkout: `{repo}`
- Interpreter: `{python}`
- Model config: `{repo}/configs/model_config.yaml`

An API key must be present, either as an environment variable or in that config
file. `OPENROUTER_API_KEY` covers both reasoning and image generation with one
key; `GOOGLE_API_KEY` uses the Gemini API directly. If both are set, OpenRouter
wins.

Re-run `tools/install-paperbanana-skill.py` from the kuroko checkout to update.
"""


def log(msg):
    print(f"  {msg}")


def step(msg):
    print(f"\n==> {msg}")


def run(cmd, **kw):
    """Run a command, streaming output, and abort on failure."""
    printable = " ".join(str(c) for c in cmd)
    log(f"$ {printable}")
    result = subprocess.run(cmd, **kw)
    if result.returncode != 0:
        sys.exit(f"\nFAILED: {printable}\nExit code {result.returncode}.")
    return result


def find_uv():
    uv = shutil.which("uv")
    if uv:
        return uv
    # uv's installer drops it here but does not always reach an existing shell.
    for candidate in (
        Path.home() / ".local" / "bin" / "uv",
        Path.home() / ".cargo" / "bin" / "uv",
        Path.home() / ".local" / "bin" / "uv.exe",
    ):
        if candidate.exists():
            return str(candidate)
    return None


def venv_python(venv_dir):
    if platform.system() == "Windows":
        return venv_dir / "Scripts" / "python.exe"
    return venv_dir / "bin" / "python"


def main():
    default_repo = Path.home() / "src" / "PaperBanana"
    default_skills = Path.home() / ".claude" / "skills"

    parser = argparse.ArgumentParser(
        description="Install the PaperBanana diagram skill for Claude Code."
    )
    parser.add_argument(
        "--repo-dir",
        type=Path,
        default=default_repo,
        help=f"Where to check out PaperBanana (default: {default_repo})",
    )
    parser.add_argument(
        "--skills-dir",
        type=Path,
        default=default_skills,
        help=f"Claude skills directory (default: {default_skills})",
    )
    parser.add_argument(
        "--skip-deps",
        action="store_true",
        help="Clone/update and write the skill, but do not build the venv.",
    )
    args = parser.parse_args()

    repo_dir = args.repo_dir.expanduser().resolve()
    skills_dir = args.skills_dir.expanduser().resolve()

    print(f"PaperBanana skill installer - {platform.system()} {platform.machine()}")
    print(f"  checkout : {repo_dir}")
    print(f"  skill    : {skills_dir / SKILL_NAME}")

    if not shutil.which("git"):
        sys.exit("git not found on PATH. Install git and re-run.")

    # --- 1. checkout -------------------------------------------------------
    step("Fetching PaperBanana")
    if (repo_dir / ".git").is_dir():
        log("Existing checkout found; updating.")
        run(["git", "-C", str(repo_dir), "pull", "--ff-only"])
    else:
        repo_dir.parent.mkdir(parents=True, exist_ok=True)
        run(["git", "clone", "--depth", "1", REPO_URL, str(repo_dir)])

    # --- 2. venv -----------------------------------------------------------
    venv_dir = repo_dir / ".venv"
    py = venv_python(venv_dir)

    if args.skip_deps:
        step("Skipping dependency install (--skip-deps)")
        if not py.exists():
            # Nothing to point the skill at yet; fall back to the current
            # interpreter so the SKILL.md is at least coherent.
            py = Path(sys.executable)
            log(f"No venv present; skill will reference {py}")
    else:
        step("Building the virtualenv")
        uv = find_uv()
        if uv:
            # PaperBanana targets 3.12; uv will fetch it if this machine lacks it.
            run([uv, "venv", "--python", "3.12", str(venv_dir)])
            run(
                [uv, "pip", "install", "--python", str(py),
                 "-r", str(repo_dir / "requirements.txt")]
            )
        else:
            log("uv not found; falling back to the stdlib venv module.")
            log("(uv is what upstream expects: https://docs.astral.sh/uv/)")
            if sys.version_info < (3, 12):
                log(
                    f"WARNING: this interpreter is {sys.version_info.major}."
                    f"{sys.version_info.minor}; PaperBanana targets 3.12. "
                    "Install uv for a correct interpreter."
                )
            run([sys.executable, "-m", "venv", str(venv_dir)])
            run([str(py), "-m", "pip", "install", "--upgrade", "pip"])
            run([str(py), "-m", "pip", "install", "-r",
                 str(repo_dir / "requirements.txt")])

    # --- 3. model config ---------------------------------------------------
    step("Model config")
    cfg = repo_dir / "configs" / "model_config.yaml"
    template = repo_dir / "configs" / "model_config.template.yaml"
    if cfg.exists():
        log(f"Already present, left alone: {cfg}")
    elif template.exists():
        shutil.copy2(template, cfg)
        log(f"Created from template: {cfg}")
    else:
        log(f"WARNING: no template at {template}")

    # --- 4. the skill file -------------------------------------------------
    step("Writing the skill")
    skill_dir = skills_dir / SKILL_NAME
    skill_dir.mkdir(parents=True, exist_ok=True)
    skill_md = skill_dir / "SKILL.md"
    skill_md.write_text(
        SKILL_TEMPLATE.format(
            python=py,
            run_py=repo_dir / "skill" / "run.py",
            repo=repo_dir,
        ),
        encoding="utf-8",
    )
    log(f"Wrote {skill_md}")

    # --- 5. credentials ----------------------------------------------------
    step("API key")
    have_or = bool(os.environ.get("OPENROUTER_API_KEY"))
    have_google = bool(os.environ.get("GOOGLE_API_KEY"))
    if have_or or have_google:
        which = "OPENROUTER_API_KEY" if have_or else "GOOGLE_API_KEY"
        if have_or and have_google:
            which = "OPENROUTER_API_KEY (takes precedence) and GOOGLE_API_KEY"
        log(f"Found in the environment: {which}")
    else:
        log("No OPENROUTER_API_KEY or GOOGLE_API_KEY in this environment.")
        log("Set one before using the skill, e.g.:")
        if platform.system() == "Windows":
            log('  setx OPENROUTER_API_KEY "sk-or-v1-..."')
        else:
            log('  export OPENROUTER_API_KEY="sk-or-v1-..."')
        log(f"or fill in api_keys: in {cfg}")

    print("\nDone. Start a new Claude session to pick up the skill.")
    print("First run downloads PaperBananaBench from HuggingFace unless you")
    print("pass --retrieval-setting none.")


if __name__ == "__main__":
    main()
