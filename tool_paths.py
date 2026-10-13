"""Installation discovery shared by investigators; never opens a game file."""
import os
from pathlib import Path

def configured(game):
    key = f"DINORAND_{game}_DIR"
    value = os.environ.get(key)
    if value is None:
        for directory in [Path.cwd(), *Path.cwd().parents, Path(__file__).parent, *Path(__file__).parents]:
            env = directory / ".env"
            if not env.is_file():
                continue
            for line in env.read_text(encoding="utf-8").splitlines():
                name, sep, val = line.strip().partition("=")
                if sep and name.strip() == key:
                    value = val.strip().strip("\"'")
                    break
            if value is not None:
                break
    return Path(value).expanduser().resolve() if value else None

def game_path(game, relative=""):
    root = configured(game)
    if root is None:
        # Importing an investigator needs no fixture. Execution fails on the absent path.
        return str(Path("__configure_" + f"DINORAND_{game}_DIR" + "__") / relative)
    candidates = [root / relative]
    prefix = "english/" if game == "DC1" else "rebirth/"
    if relative.startswith(prefix):
        candidates.append(root / relative[len(prefix):])
    if relative.endswith("/Data"):
        candidates.append(root)
    return str(next((p for p in candidates if p.exists()), candidates[0]))

def refuse_install_output(path, *inputs):
    target = Path(path).resolve()
    for game in ("DC1", "DC2"):
        root = configured(game)
        if root and (target == root or root in target.parents):
            raise ValueError("Refusing output inside original installation")
    if any(target == Path(source).resolve() for source in inputs):
        raise ValueError("Refusing to overwrite input")
    return target
