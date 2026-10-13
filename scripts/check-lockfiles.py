from pathlib import Path
import json
import subprocess
import xml.etree.ElementTree as ET

def tracked_projects(root):
    # Tracked files only: ignored local scratch projects are not part of the snapshot.
    # Outside a git checkout (an exported tree, a test fixture) every project counts.
    git = subprocess.run(['git', '-C', str(root), 'ls-files', '-z', '--', '*.csproj'],
                         capture_output=True, text=True)
    if git.returncode != 0:
        return list(root.rglob('*.csproj'))
    return [root / name for name in git.stdout.split('\0') if name]


def check(root):
    for project in tracked_projects(root):
        references = {n.attrib['Include']: n.attrib['Version'] for n in ET.parse(project).getroot().iter('PackageReference')}
        lock = project.with_name('packages.lock.json')
        if not lock.is_file():
            raise ValueError(f'Missing lockfile: {project}')
        dependencies = json.loads(lock.read_text())['dependencies']
        for packages in dependencies.values():
            for name, version in references.items():
                item = packages.get(name, {})
                if item.get('type') != 'Direct' or item.get('requested') != f'[{version}, )':
                    raise ValueError(f'Dependency lock drift: {project}: {name}')
    print('Direct dependency locks verified; locked restore verifies transitive graph and hashes')

if __name__ == '__main__':
    try:
        check(Path(__file__).resolve().parents[1])
    except ValueError as error:
        raise SystemExit(str(error))
