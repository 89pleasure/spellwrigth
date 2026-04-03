# Hinweise für KI-Agenten – Spellwright / CityBuilder

Diese Datei sammelt Fehler und Fallstricke, die in früheren Sessions aufgetreten sind.
Lies sie vor jeder Implementierung die Physics, Mesh-Generierung oder Kollision betrifft.

---

## MeshCollider: Inside-Out-Winding → Physics.Raycast trifft nichts

### Problem
Unity's `Physics.queriesHitBackfaces` ist standardmäßig `false`.
PhysX ignoriert Back Faces vollständig – kein Raycast-Hit, keine Fehlermeldung.

Ein Mesh dessen Triangles nach **innen** zeigen (CW-Winding von außen) ist für
`Physics.Raycast` komplett unsichtbar, obwohl:
- `MeshCollider.enabled = true`
- `MeshCollider.sharedMesh` korrekt gesetzt ist
- `mc.bounds` die richtige Weltposition zeigt
- `mc.sharedMesh.isReadable = true`
- `mc.isTrigger = false`
- Der Layer-Mask-Wert stimmt

### Ursache (konkret in RoadCollisionBuilder)
`AddQuad(a, b, c, d)` erzeugt `(a,b,c)` und `(c,b,d)`.
Die Reihenfolge der vier Parameter bestimmt die Winding-Order.

Falsch (Inside-Out):
```csharp
AddQuad(tris, a + 0, b + 0, a + 1, b + 1); // top face
```

Korrekt (Outward Normal +Y):
```csharp
AddQuad(tris, a + 0, a + 1, b + 0, b + 1); // top face
```

### Faustregel für Prismen-Meshes
Für ein Prismenviereck mit Vertices TL/TR (oben) und BL/BR (unten),
das sich von Sample `curr` nach Sample `next` erstreckt:

| Face | Gewünschte Normale | Korrekter AddQuad-Aufruf |
|---|---|---|
| Top | +Y (oben) | `AddQuad(TL_c, TR_c, TL_n, TR_n)` |
| Bottom | −Y (unten) | `AddQuad(BL_c, BL_n, BR_c, BR_n)` |
| Left wall | −right | `AddQuad(TL_c, TL_n, BL_c, BL_n)` |
| Right wall | +right | `AddQuad(TR_c, BR_c, TR_n, BR_n)` |
| Front cap | −forward | `AddQuad(TL, BL, TR, BR)` |
| Back cap | +forward | `AddQuad(TL, TR, BL, BR)` |

### Diagnose-Strategie
Wenn `Physics.Raycast` keinen MeshCollider trifft obwohl Bounds und Mesh korrekt aussehen:
1. Einen Raycast **senkrecht von oben** direkt über dem `mc.bounds.center` feuern
2. Trifft er Terrain/andere Objekte statt den Collider → Inside-Out-Winding
3. Fix: Winding korrigieren **oder** `Physics.queriesHitBackfaces = true` setzen

---

## LSP-Fehler in RoadCollisionBuilder.cs (False Positives)

Die Fehler `float3 not found`, `BezierCurve not found`, `math not found` im LSP
für `RoadCollisionBuilder.cs` sind **False Positives**.

Die Datei liegt im selben Assembly-Definition-Scope wie `RoadMeshBuilder.cs`,
der dieselben Types ohne Fehler nutzt. Der Unity-LSP kennt das Assembly ohne
vollständigen Reload nicht. Unity kompiliert die Datei korrekt.
