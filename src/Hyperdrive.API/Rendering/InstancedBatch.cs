using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hyperdrive.API.Rendering
{
    /// <summary>
    /// Draws many copies of one mesh with one material in as few draw calls as possible (GPU instancing,
    /// up to 1023 instances per call), with an optional per-instance color. Falls back to one Graphics.DrawMesh per
    /// instance when instancing is unavailable (unsupported GPU, or material without "Enable GPU Instancing").
    /// Fill it every frame (Clear, Add...) and call <see cref="Draw"/> from Update/LateUpdate: allocation-free.
    /// Mac note: KSP runs OpenGL 4.1 there (no compute shaders), so only this DrawMeshInstanced path exists.
    /// </summary>
    public sealed class InstancedBatch
    {
        public const int MaxPerDraw = 1023;

        private readonly Mesh _mesh;
        private readonly Material _material;
        private readonly int _submesh;
        private readonly int _colorId;
        private readonly Matrix4x4[] _matrices;
        private readonly Vector4[] _colors;
        private readonly Matrix4x4[] _chunkMatrices = new Matrix4x4[MaxPerDraw];
        private readonly Vector4[] _chunkColors = new Vector4[MaxPerDraw];
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        private readonly bool _useColor;
        private int _count;

        /// <param name="capacity">Maximum instances per frame (extra Add calls are ignored).</param>
        /// <param name="colorProperty">Per-instance color property declared with UNITY_INSTANCING_BUFFER in the shader, or null.</param>
        public InstancedBatch(Mesh mesh, Material material, int capacity, string colorProperty = "_Color", int submeshIndex = 0)
        {
            _mesh = mesh != null ? mesh : throw new ArgumentNullException(nameof(mesh));
            _material = material != null ? material : throw new ArgumentNullException(nameof(material));
            _submesh = submeshIndex;
            capacity = Math.Max(1, capacity);
            _matrices = new Matrix4x4[capacity];
            _useColor = !string.IsNullOrEmpty(colorProperty);
            _colors = _useColor ? new Vector4[capacity] : null;
            _colorId = _useColor ? Shader.PropertyToID(colorProperty) : 0;
        }

        public int Count => _count;

        public int Capacity => _matrices.Length;

        /// <summary>True when draws go through DrawMeshInstanced, false when using the per-instance fallback.</summary>
        public bool UsesInstancing => SystemInfo.supportsInstancing && _material.enableInstancing;

        public void Clear() => _count = 0;

        public bool Add(Matrix4x4 matrix)
        {
            if (_count >= _matrices.Length)
                return false;
            _matrices[_count] = matrix;
            if (_useColor)
                _colors[_count] = Color.white;
            _count++;
            return true;
        }

        public bool Add(Matrix4x4 matrix, Color color)
        {
            if (_count >= _matrices.Length)
                return false;
            _matrices[_count] = matrix;
            if (_useColor)
                _colors[_count] = color;
            _count++;
            return true;
        }

        public bool Add(Vector3 position, Quaternion rotation, Vector3 scale, Color color) =>
            Add(Matrix4x4.TRS(position, rotation, scale), color);

        public void Draw(int layer = 0, Camera camera = null, ShadowCastingMode shadows = ShadowCastingMode.Off, bool receiveShadows = false)
        {
            if (_count == 0)
                return;

            if (UsesInstancing)
            {
                for (int start = 0; start < _count; start += MaxPerDraw)
                {
                    int n = Math.Min(MaxPerDraw, _count - start);
                    Array.Copy(_matrices, start, _chunkMatrices, 0, n);
                    if (_useColor)
                    {
                        // The array length bound to a property block is fixed at first use: always pass MaxPerDraw.
                        Array.Copy(_colors, start, _chunkColors, 0, n);
                        _block.SetVectorArray(_colorId, _chunkColors);
                    }
                    Graphics.DrawMeshInstanced(_mesh, _submesh, _material, _chunkMatrices, n, _block, shadows, receiveShadows, layer, camera);
                }
            }
            else
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_useColor)
                        _block.SetColor(_colorId, _colors[i]);
                    Graphics.DrawMesh(_mesh, _matrices[i], _material, layer, camera, _submesh, _block, shadows, receiveShadows);
                }
            }
        }
    }
}
