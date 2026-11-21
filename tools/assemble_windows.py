"""Assemble a Windows server ZIP from a Release build and a clean CPU-wheel environment."""
import argparse
from importlib import metadata
from pathlib import Path
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PLUGIN = "addons/counterstrikesharp/plugins/BotBuy/"
PYTHON = PLUGIN + "planner/python/"


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--plugin-build", type=Path, required=True)
    cli.add_argument("--embed", type=Path, required=True)
    cli.add_argument("--output", type=Path, required=True)
    args = cli.parse_args()
    if sys.platform != "win32" or sys.version_info[:2] != (3,12):
        cli.error("Build using Windows x64 Python 3.12")
    import torch
    if torch.version.cuda is not None:
        cli.error("Use the CPU build of PyTorch")
    args.output.parent.mkdir(parents=True,exist_ok=True)
    temp = args.output.with_suffix(".partial")
    with zipfile.ZipFile(temp,"w",zipfile.ZIP_DEFLATED,compresslevel=5) as archive:
        for name in ("BotBuy.dll","BotBuy.deps.json","buy-policy.json","pro-transformer.json",
                     "pro-primary-policy.json","bot-native.json"):
            archive.write(args.plugin_build/name,PLUGIN+name)
        for file in sorted((ROOT/"pro_buy").glob("*.py")):
            archive.write(file,PLUGIN+"planner/pro_buy/"+file.name)
        for file in sorted((ROOT/"models").glob("*.pt")):
            archive.write(file,PLUGIN+"planner/models/"+file.name)
        for file in ("LICENSE","LICENSES/GPL-3.0.txt","NOTICE.md","README.md"):
            archive.write(ROOT/file,PLUGIN+file)
        with zipfile.ZipFile(args.embed) as embed:
            for f in embed.infolist():
                if not f.is_dir() and f.filename!="python312._pth":
                    archive.writestr(PYTHON+f.filename,embed.read(f))
            if "python312.zip" not in embed.namelist():
                raise ValueError("Expected the official CPython 3.12 x64 embeddable distribution")
        archive.writestr(PYTHON+"python312._pth","python312.zip\n.\nLib/site-packages\n../pro_buy\n")
        requirements=(ROOT/"requirements-runtime.txt").read_text().splitlines()
        for requirement in requirements:
            if not requirement or requirement.startswith("#"): continue
            name,version=requirement.split("==")
            dist=metadata.distribution(name)
            if dist.version.split("+")[0]!=version:
                raise ValueError(f"Expected {requirement}, found {dist.version}")
            for entry in dist.files:
                if ".." in entry.parts or "__pycache__" in entry.parts or entry.suffix==".pyc":
                    continue
                file=Path(dist.locate_file(entry))
                archive.write(file,PYTHON+"Lib/site-packages/"+entry.as_posix())
    temp.replace(args.output)
    print(args.output.resolve())


if __name__=="__main__":
    main()
