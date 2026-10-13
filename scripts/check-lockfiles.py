from pathlib import Path
import json
import xml.etree.ElementTree as ET

def check(root):
    for project in root.rglob('*.csproj'):
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
