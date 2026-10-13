"""Deterministic, non-interactive glTF import/export witness for Blender 5.2 LTS."""

import json
import os
from pathlib import Path
import sys

import bpy


def _arguments():
    try:
        separator = sys.argv.index("--")
    except ValueError as error:
        raise RuntimeError("expected '-- <input.glb> <output.glb> <result.json>'") from error

    arguments = sys.argv[separator + 1 :]
    if len(arguments) != 3:
        raise RuntimeError("expected exactly: <input.glb> <output.glb> <result.json>")
    return tuple(Path(value).resolve() for value in arguments)


def _scene_snapshot():
    all_meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    meshes = [
        obj
        for obj in all_meshes
        if any(modifier.type == "ARMATURE" for modifier in obj.modifiers)
    ]
    armatures = [obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE"]
    aligned_triangles = 0
    opposed_triangles = 0
    degenerate_triangles = 0
    triangles = 0
    for obj in meshes:
        mesh = obj.data
        mesh.calc_loop_triangles()
        corner_normals = mesh.corner_normals
        for triangle in mesh.loop_triangles:
            triangles += 1
            loop_indices = triangle.loops
            points = [
                mesh.vertices[mesh.loops[loop_index].vertex_index].co
                for loop_index in loop_indices
            ]
            geometric = (points[1] - points[0]).cross(points[2] - points[0])
            if geometric.length_squared < 1e-12:
                degenerate_triangles += 1
                continue
            custom = sum(
                (corner_normals[loop_index].vector for loop_index in loop_indices),
                start=points[0] * 0,
            )
            dot = geometric.dot(custom)
            if dot > 0:
                aligned_triangles += 1
            elif dot < 0:
                opposed_triangles += 1

    image_nodes = [
        node
        for material in bpy.data.materials
        if material.node_tree is not None
        for node in material.node_tree.nodes
        if node.type == "TEX_IMAGE"
    ]
    return {
        "alignedTriangles": aligned_triangles,
        "armatures": len(armatures),
        "auxiliaryMeshes": len(all_meshes) - len(meshes),
        "clampToEdgeImageTextures": sum(node.extension == "EXTEND" for node in image_nodes),
        "degenerateTriangles": degenerate_triangles,
        "images": len(bpy.data.images),
        "jointNames": sorted(
            bone.name
            for armature in armatures
            for bone in armature.data.bones
            if bone.name.startswith("joint_")
        ),
        "materials": len(bpy.data.materials),
        "meshDetails": [
            {
                "name": obj.name,
                "polygons": len(obj.data.polygons),
                "uvLayers": len(obj.data.uv_layers),
                "vertices": len(obj.data.vertices),
            }
            for obj in meshes
        ],
        "meshes": len(meshes),
        "nearestImageTextures": sum(node.interpolation == "Closest" for node in image_nodes),
        "opposedTriangles": opposed_triangles,
        "polygons": sum(len(obj.data.polygons) for obj in meshes),
        "singleSidedMaterials": sum(
            bool(getattr(material, "use_backface_culling", False))
            for material in bpy.data.materials
        ),
        "triangles": triangles,
        "uvLayers": sum(len(obj.data.uv_layers) for obj in meshes),
        "uvLoops": sum(
            len(layer.data)
            for obj in meshes
            for layer in obj.data.uv_layers
        ),
        "vertices": sum(len(obj.data.vertices) for obj in meshes),
    }


def _clear_factory_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for mesh in tuple(bpy.data.meshes):
        if mesh.users == 0:
            bpy.data.meshes.remove(mesh)
    for material in tuple(bpy.data.materials):
        if material.users == 0:
            bpy.data.materials.remove(material)


def _root_skinned_meshes():
    # Blender imports a skinned glTF mesh as a child of the armature object. glTF ignores parent
    # transforms on skinned meshes and Khronos Validator flags that nesting, so preserve the world
    # transform while making each armature-modified mesh a scene root before export.
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue
        if not any(modifier.type == "ARMATURE" for modifier in obj.modifiers):
            continue
        world = obj.matrix_world.copy()
        obj.parent = None
        obj.matrix_world = world


def main():
    input_path, output_path, result_path = _arguments()
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
    from tool_paths import refuse_install_output
    refuse_install_output(output_path, input_path)
    refuse_install_output(result_path, input_path, output_path)
    output_temporary = output_path.with_name(
        f".{output_path.stem}.{os.getpid()}.tmp{output_path.suffix}"
    )
    result_temporary = result_path.with_name(
        f".{result_path.stem}.{os.getpid()}.tmp{result_path.suffix}"
    )
    output_moved = False

    try:
        if input_path == output_path:
            raise RuntimeError("input and output paths must differ")
        if not input_path.is_file():
            raise RuntimeError(f"input does not exist: {input_path.name}")
        if output_path.exists() or result_path.exists():
            raise RuntimeError("output and result paths must not already exist")
        if not output_path.parent.is_dir() or not result_path.parent.is_dir():
            raise RuntimeError("output and result parent directories must already exist")

        _clear_factory_scene()
        imported = bpy.ops.import_scene.gltf(
            filepath=str(input_path),
            bone_heuristic="BLENDER",
        )
        if imported != {"FINISHED"}:
            raise RuntimeError(f"glTF import did not finish: {sorted(imported)}")

        _root_skinned_meshes()
        scene = _scene_snapshot()
        exported = bpy.ops.export_scene.gltf(
            filepath=str(output_temporary),
            export_format="GLB",
            export_animations=False,
            export_skins=True,
            export_yup=True,
        )
        if exported != {"FINISHED"}:
            raise RuntimeError(f"glTF export did not finish: {sorted(exported)}")

        result = {
            "blenderVersion": bpy.app.version_string,
            "boneHeuristic": "BLENDER",
            "input": input_path.name,
            "output": output_path.name,
            "scene": scene,
            "schemaVersion": 1,
            "status": "ok",
        }
        result_temporary.write_text(
            json.dumps(result, sort_keys=True, separators=(",", ":")) + "\n",
            encoding="utf-8",
        )
        os.replace(output_temporary, output_path)
        output_moved = True
        os.replace(result_temporary, result_path)
    except Exception as error:
        for candidate in (output_temporary, result_temporary):
            if candidate.exists():
                candidate.unlink()
        if output_moved and output_path.exists() and not result_path.exists():
            output_path.unlink()
        if result_path.parent.is_dir() and not result_path.exists():
            failure = {
                "blenderVersion": bpy.app.version_string,
                "boneHeuristic": "BLENDER",
                "error": str(error),
                "input": input_path.name,
                "output": output_path.name,
                "schemaVersion": 1,
                "status": "error",
            }
            result_temporary.write_text(
                json.dumps(failure, sort_keys=True, separators=(",", ":")) + "\n",
                encoding="utf-8",
            )
            os.replace(result_temporary, result_path)
        raise


if __name__ == "__main__":
    main()
