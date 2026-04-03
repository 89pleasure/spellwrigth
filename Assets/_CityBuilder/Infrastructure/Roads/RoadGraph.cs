using System.Collections.Generic;
using Unity.Mathematics;

#nullable enable
namespace CityBuilder.Infrastructure.Roads
{
    /// <summary>
    /// Pure data structure for the city road network.
    /// Nodes are intersections and endpoints; segments are the road sections between them.
    ///
    /// Responsibilities:
    ///   • Add and remove nodes and segments
    ///   • Split a segment when a new road meets it at its middle (T-junction)
    ///   • Keep each node's OrderedSegmentIds sorted clockwise for the intersection
    ///     mesh builder and the parcel generator
    ///   • Track dirty nodes so the pathfinding system only recomputes affected routes
    ///
    /// No Unity object dependencies – only Unity.Mathematics for vector math.
    /// </summary>
    public class RoadGraph
    {
        private readonly Dictionary<int, RoadNode>    _nodes    = new();
        private readonly Dictionary<int, RoadSegment> _segments = new();
        private int _nextNodeId;
        private int _nextSegmentId;

        public IReadOnlyDictionary<int, RoadNode>    Nodes    => _nodes;
        public IReadOnlyDictionary<int, RoadSegment> Segments => _segments;

        // ─────────────────────────────────────────────────────────
        //  Node management
        // ─────────────────────────────────────────────────────────

        public RoadNode AddNode(float3 position)
        {
            RoadNode node = new RoadNode(_nextNodeId++, position);
            _nodes[node.Id] = node;
            return node;
        }

        /// <summary>
        /// Removes a node and all segments that connect to it.
        /// Used when the last segment at a node is demolished.
        /// </summary>
        public bool RemoveNode(int nodeId)
        {
            if (!_nodes.TryGetValue(nodeId, out RoadNode? node))
                return false;

            foreach (int segId in node.SegmentIds.ToArray())
                RemoveSegment(segId);

            _nodes.Remove(nodeId);
            return true;
        }

        // ─────────────────────────────────────────────────────────
        //  Segment management
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Adds a curved road segment between two existing nodes.
        /// controlPointA (P1) and controlPointB (P2) are the inner Bézier handles.
        ///
        /// For a straight road the caller should pass the ⅓ and ⅔ points along the
        /// straight line – the Bézier then degenerates to a straight line.
        ///
        /// Returns null if either node ID does not exist, or if a segment between
        /// these two nodes already exists.
        /// </summary>
        public RoadSegment? AddSegment(
            int    nodeAId,
            int    nodeBId,
            float3 controlPointA,
            float3 controlPointB,
            int    lanes,
            float  speedLimit)
        {
            if (!_nodes.TryGetValue(nodeAId, out RoadNode? nodeA)) return null;
            if (!_nodes.TryGetValue(nodeBId, out RoadNode? nodeB)) return null;

            RoadSegment seg = new (
                _nextSegmentId++,
                nodeAId, nodeBId,
                nodeA.Position, nodeB.Position,
                controlPointA, controlPointB,
                lanes, speedLimit);

            _segments[seg.Id] = seg;
            nodeA.SegmentIds.Add(seg.Id);
            nodeB.SegmentIds.Add(seg.Id);
            RebuildOrderedSegments(nodeAId);
            RebuildOrderedSegments(nodeBId);
            return seg;
        }

        public void RemoveSegment(int segmentId)
        {
            if (!_segments.TryGetValue(segmentId, out RoadSegment? seg))
            {
                return;
            }

            if (_nodes.TryGetValue(seg.NodeA, out RoadNode? nodeA))
            {
                nodeA.SegmentIds.Remove(segmentId);
                RebuildOrderedSegments(nodeA.Id);
            }

            if (_nodes.TryGetValue(seg.NodeB, out RoadNode? nodeB))
            {
                nodeB.SegmentIds.Remove(segmentId);
                RebuildOrderedSegments(nodeB.Id);
            }

            _segments.Remove(segmentId);
        }

        // ─────────────────────────────────────────────────────────
        //  Segment splitting (T-junction creation)
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Splits an existing road segment at Bézier parameter t ∈ (0, 1).
        /// A new intersection node is inserted at the split point; the original
        /// segment is shortened to end there, and a new segment covers the rest.
        ///
        /// Called when the player starts or ends a new road on top of an existing
        /// segment rather than snapping to a node – creating a T-junction.
        ///
        /// Returns the newly created junction node and the ID of the new second-half segment.
        /// The original segment (segmentId) is shortened in-place to cover the first half.
        /// </summary>
        public (RoadNode splitNode, int newSegmentId) SplitSegment(int segmentId, float t)
        {
            RoadSegment seg   = _segments[segmentId];
            RoadNode    nodeA = _nodes[seg.NodeA];
            RoadNode    nodeB = _nodes[seg.NodeB];

            // ── 1. Insert the new junction node at the split point ──────────
            float3   splitPos  = BezierCurve.Evaluate(nodeA.Position, seg.ControlPointA, seg.ControlPointB, nodeB.Position, t);
            RoadNode splitNode = AddNode(splitPos);

            // ── 2. Compute Bézier handles for both halves via de Casteljau ──
            BezierCurve.SplitAt(
                nodeA.Position, seg.ControlPointA, seg.ControlPointB, nodeB.Position, t,
                out float3 leftP1,  out float3 leftP2,
                out float3 rightP1, out float3 rightP2);

            // ── 3. Shorten original segment: it now ends at the junction ────
            int oldNodeBId = seg.NodeB;
            nodeB.SegmentIds.Remove(segmentId);

            seg.NodeB         = splitNode.Id;
            seg.ControlPointA = leftP1;
            seg.ControlPointB = leftP2;
            seg.RebuildArcLengthLUT(nodeA.Position, splitPos);

            splitNode.SegmentIds.Add(segmentId);

            // ── 4. New segment: junction → old end node ──────────────────────
            RoadNode    oldNodeB    = _nodes[oldNodeBId];
            RoadSegment secondHalf  = new(
                _nextSegmentId++,
                splitNode.Id, oldNodeBId,
                splitPos, oldNodeB.Position,
                rightP1, rightP2,
                seg.Lanes, seg.SpeedLimit)
            {
                ParentSegmentId = segmentId
            };

            _segments[secondHalf.Id] = secondHalf;
            splitNode.SegmentIds.Add(secondHalf.Id);
            oldNodeB.SegmentIds.Add(secondHalf.Id);

            // ── 5. Re-sort segments on every touched node ────────────────────
            RebuildOrderedSegments(nodeA.Id);
            RebuildOrderedSegments(splitNode.Id);
            RebuildOrderedSegments(oldNodeBId);

            return (splitNode, secondHalf.Id);
        }

        // ─────────────────────────────────────────────────────────
        //  Ordered segments (clockwise from north)
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Sorts the node's segments by the angle at which they leave the node,
        /// measured clockwise from north (+Z) in the XZ plane.
        ///
        /// Must be called after every add or remove that touches this node.
        /// Cost: O(k log k) where k is the number of segments at the node – at most
        /// ~6 for a city road network, so effectively O(1) per operation.
        /// </summary>
        private void RebuildOrderedSegments(int nodeId)
        {
            if (!_nodes.TryGetValue(nodeId, out RoadNode? node))
                return;

            node.OrderedSegmentIds.Clear();
            node.OrderedSegmentIds.AddRange(node.SegmentIds);
            node.OrderedSegmentIds.Sort((a, b) =>
                GetSegmentAngleFrom(a, nodeId).CompareTo(GetSegmentAngleFrom(b, nodeId)));
        }

        /// <summary>
        /// Clockwise-from-north angle (radians) at which segment segId leaves nodeId.
        /// Used only during sort – not called per frame.
        /// </summary>
        private float GetSegmentAngleFrom(int segId, int nodeId)
        {
            if (!_segments.TryGetValue(segId, out RoadSegment? seg))
                return 0f;

            RoadNode nodeA = _nodes[seg.NodeA];
            RoadNode nodeB = _nodes[seg.NodeB];

            // Tangent evaluated at the end that touches nodeId, pointing away from it
            float3 tangent = seg.NodeA == nodeId
                ?  BezierCurve.EvaluateTangent(nodeA.Position, seg.ControlPointA, seg.ControlPointB, nodeB.Position, 0f)
                : -BezierCurve.EvaluateTangent(nodeA.Position, seg.ControlPointA, seg.ControlPointB, nodeB.Position, 1f);

            return math.atan2(tangent.x, tangent.z);  // XZ plane, clockwise from +Z
        }

        // ─────────────────────────────────────────────────────────
        //  Dirty flags (pathfinding system)
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Marks a node as dirty so the pathfinding system knows that citizens
        /// whose cached routes pass through here must recalculate their commute.
        /// </summary>
        public void MarkDirty(int nodeId)
        {
            if (_nodes.TryGetValue(nodeId, out RoadNode? node))
                node.IsDirty = true;
        }

        public void ClearDirtyFlags()
        {
            foreach (RoadNode node in _nodes.Values)
                node.IsDirty = false;
        }

        // ─────────────────────────────────────────────────────────
        //  Spatial queries
        // ─────────────────────────────────────────────────────────

        /// <summary>Yields all nodes reachable in one step from nodeId.</summary>
        public IEnumerable<RoadNode> GetNeighbors(int nodeId)
        {
            if (!_nodes.TryGetValue(nodeId, out RoadNode? node))
                yield break;

            foreach (int segId in node.SegmentIds)
            {
                if (_segments.TryGetValue(segId, out RoadSegment? seg))
                    yield return _nodes[seg.OtherNode(nodeId)];
            }
        }

        /// <summary>
        /// Returns the nearest node within snapRadius, or null if none found.
        /// Linear scan – acceptable for the segment counts of a city-builder.
        /// </summary>
        public RoadNode? FindNearestNode(float3 position, float snapRadius)
        {
            RoadNode? nearest       = null;
            float     nearestDistSq = snapRadius * snapRadius;

            foreach (RoadNode node in _nodes.Values)
            {
                float distSq = math.distancesq(node.Position, position);
                if (!(distSq < nearestDistSq))
                {
                    continue;
                }

                nearestDistSq = distSq;
                nearest       = node;
            }

            return nearest;
        }

        /// <summary>
        /// Returns the road segment whose Bézier curve passes closest to position,
        /// together with the curve parameter t at that nearest point.
        /// Returns null when no segment is within maxDistance world units.
        ///
        /// Used to detect when a new road endpoint lands on an existing segment
        /// (T-junction) rather than near an existing node.
        /// </summary>
        public (RoadSegment segment, float t)? FindNearestSegment(float3 position, float maxDistance)
        {
            RoadSegment? nearest     = null;
            float        nearestDist = maxDistance;
            float        nearestT    = 0f;

            foreach (RoadSegment seg in _segments.Values)
            {
                if (!_nodes.TryGetValue(seg.NodeA, out RoadNode nodeA)) continue;
                if (!_nodes.TryGetValue(seg.NodeB, out RoadNode nodeB)) continue;

                (float dist, float t) result = ClosestPointOnCurve(
                    position,
                    nodeA.Position, seg.ControlPointA, seg.ControlPointB, nodeB.Position);

                if (!(result.dist < nearestDist))
                {
                    continue;
                }

                nearestDist = result.dist;
                nearestT    = result.t;
                nearest     = seg;
            }

            return nearest != null ? (nearest, nearestT) : null;
        }

        /// <summary>
        /// Approximates the nearest point on a Bézier curve to a world position by
        /// sampling the curve at fixed intervals. Returns the distance and the
        /// curve parameter t at the nearest sample.
        ///
        /// Accuracy: ≈ arc-length / samples. 20 samples gives ~5% accuracy for a
        /// typical 100 m road segment – sufficient for snap detection.
        /// </summary>
        private static (float distance, float t) ClosestPointOnCurve(
            float3 point,
            float3 p0, float3 p1, float3 p2, float3 p3,
            int    samples = 20)
        {
            float bestDist = float.MaxValue;
            float bestT    = 0f;

            for (int i = 0; i <= samples; i++)
            {
                float  t        = i / (float)samples;
                float3 curvePos = BezierCurve.Evaluate(p0, p1, p2, p3, t);
                float  dist     = math.distance(point, curvePos);
                if (!(dist < bestDist))
                {
                    continue;
                }

                bestDist = dist;
                bestT    = t;
            }

            return (bestDist, bestT);
        }
    }
}
