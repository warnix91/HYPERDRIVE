# Using Hyperdrive from another mod (soft dependency)

Your mod must work **with or without** Hyperdrive installed.

1. Reference `GameData/Hyperdrive/Plugins/Hyperdrive.API.dll` with `Private=false` (never ship it with your mod).
2. Keep every use of `Hyperdrive.API` in a separate class, called only when the assembly is loaded:

```csharp
static bool HyperdrivePresent()
{
    foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
        if (a.name == "Hyperdrive.API") return true;
    return false;
}

// A separate class: the JIT resolves the Hyperdrive types only if it is called.
static class HyperdriveBridge
{
    public static bool LogGuardActive() =>
        Hyperdrive.API.HyperdriveAPI.IsReady &&
        Hyperdrive.API.HyperdriveAPI.IsSubsystemActive("LogSpamGuard");
}
```

3. Check `HyperdriveAPI.InstalledApiVersion` (not the `ApiVersion` constant) if you rely on a recent feature.

## Available
- **ApiVersion 1**: `HyperdriveAPI.IsReady`, `RuntimeVersion`, `IsSubsystemActive(name)`, `Ready` event.
- **ApiVersion 2**: `HyperdriveAPI.RequestGcHold(float seconds)` holds the garbage collector for up to `seconds` (capped by
  the config, 4 s by default), right before an allocation spike or an expensive effect. Main thread only, no allocation.
  Returns `false` when refused (cooldown, scene load, GCWindows off).

```csharp
// Right before a big explosion:
static class HyperdriveBridge
{
    public static void ProtectExplosion()
    {
        // InstalledApiVersion is read at run time; ApiVersion is a constant frozen when your mod was compiled.
        if (Hyperdrive.API.HyperdriveAPI.InstalledApiVersion >= 2)
            Hyperdrive.API.HyperdriveAPI.RequestGcHold(2f);
    }
}
```

- **ApiVersion 3**:
  - `HyperdriveAPI.RegisterLight(Light, priority)` / `UnregisterLight(Light)`: puts a light under the light budget
    (LightBudget).
  - `Hyperdrive.API.Pooling.ObjectPool<T>`: a pool of C# objects (`Prewarm`, `Get`, `Release`, `Misses` counter).
  - `Hyperdrive.API.Pooling.GameObjectPool`: a pool of prefab instances (`Prewarm`, `Get(pos, rot)`, `Release`,
    `ReleaseAfter(go, s)` without a coroutine, destroyed instances detected).
  - `Hyperdrive.API.Rendering.InstancedBatch`: `Clear` / `Add(matrix, color)` / `Draw()` every frame;
    `DrawMeshInstanced` in batches of 1023, `DrawMesh` fallback without instancing. The material needs *Enable GPU
    Instancing* and a compatible shader (colour property declared in `UNITY_INSTANCING_BUFFER`).

These three classes are **standalone**: they only need `Hyperdrive.API.dll` installed, not the Hyperdrive runtime.

```csharp
// A pooled explosion:
GameObjectPool _pool;                       // created when the scene loads
void OnSceneStart() { _pool = new GameObjectPool(explosionPrefab, maxInactive: 32,
                          onGet: go => go.GetComponent<ParticleSystem>().Play(true)); _pool.Prewarm(16); }
void Explode(Vector3 pos)
{
    HyperdriveAPI.RequestGcHold(2f);        // no GC cycle during the spike
    GameObject fx = _pool.Get(pos, Quaternion.identity);
    HyperdriveAPI.RegisterLight(fx.GetComponentInChildren<Light>(), priority: 2f);
    _pool.ReleaseAfter(fx, 6f);             // UnregisterLight is best done in onRelease
}
```
