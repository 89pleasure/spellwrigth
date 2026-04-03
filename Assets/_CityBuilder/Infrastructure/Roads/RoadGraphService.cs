using CityBuilder.Core.EventBus;
using Unity.Mathematics;

#nullable enable
namespace CityBuilder.Infrastructure.Roads
{
    /// <summary>
    /// Service layer for road construction and demolition.
    ///
    /// Owns the RoadGraph and acts as the single entry point for all road changes.
    /// Responsibilities:
    ///   • Snap road endpoints to nearby existing nodes
    ///   • Detect T-junctions: when an endpoint lands on an existing segment,
    ///     split that segment and insert a new intersection node
    ///   • Compute default Bézier control points for straight roads
    ///   • Mark affected nodes dirty for the pathfinding system
    ///   • Publish road events so all other systems stay in sync
    /// </summary>
    public class RoadGraphService
    {
        // Snap to an existing node when the endpoint is within this distance (metres).
        private const float SnapRadius = 5f;

        // Only split an existing segment if the hit parameter is away from both
        // endpoints; avoids creating near-zero-length segments at the tips.
        private const float SplitEdgeGuard = 0.05f;

        public readonly RoadGraph Graph;
        private readonly EventBus _eventBus;

        public RoadGraphService(EventBus eventBus)
        {
            Graph     = new RoadGraph();
            _eventBus = eventBus;
        }

        // ─────────────────────────────────────────────────────────
        //  Road construction
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Places a road segment between two world positions.
        ///
        /// Each endpoint is resolved in order:
        ///   1. Snap to the nearest existing node within SnapRadius.
        ///   2. If no node is nearby but an existing segment is, split that segment
        ///      and use the resulting T-junction node.
        ///   3. Otherwise, create a new isolated node at the given position.
        ///
        /// Returns null if both endpoints resolve to the same node (zero-length road).
        /// </summary>
        public void BuildRoad(
            float3 from,
            float3 to,
            float gameTime,
            int lanes = 2,
            float speedLimit = 50f)
        {
            RoadNode nodeA = ResolveOrCreateNode(from, gameTime, lanes, speedLimit);
            RoadNode nodeB = ResolveOrCreateNode(to, gameTime, lanes, speedLimit);

            if (nodeA.Id == nodeB.Id) { return; }

            // Default control points for a straight road: ⅓ and ⅔ along the line.
            // The Bézier degenerates to a straight line – no special-casing needed elsewhere.
            float3 controlA = math.lerp(nodeA.Position, nodeB.Position, 1f / 3f);
            float3 controlB = math.lerp(nodeA.Position, nodeB.Position, 2f / 3f);

            BuildRoadInternal(nodeA, nodeB, controlA, controlB, lanes, speedLimit, gameTime);
        }

        /// <summary>
        /// Places a curved road segment with explicit inner Bézier handles.
        ///
        /// Use this overload when the caller has already computed the cubic control
        /// points (e.g. converted from a quadratic guide point via the 2/3-rule).
        /// </summary>
        public void BuildRoad(
            float3 from,
            float3 to,
            float3 controlA,
            float3 controlB,
            float gameTime,
            int lanes = 2,
            float speedLimit = 50f)
        {
            RoadNode nodeA = ResolveOrCreateNode(from, gameTime, lanes, speedLimit);
            RoadNode nodeB = ResolveOrCreateNode(to, gameTime, lanes, speedLimit);

            if (nodeA.Id == nodeB.Id) { return; }

            BuildRoadInternal(nodeA, nodeB, controlA, controlB, lanes, speedLimit, gameTime);
        }

        private void BuildRoadInternal(
            RoadNode nodeA,
            RoadNode nodeB,
            float3 controlA,
            float3 controlB,
            int lanes,
            float speedLimit,
            float gameTime)
        {
            RoadSegment? segment = Graph.AddSegment(nodeA.Id, nodeB.Id, controlA, controlB, lanes, speedLimit);
            if (segment == null) { return; }

            Graph.MarkDirty(nodeA.Id);
            Graph.MarkDirty(nodeB.Id);

            _eventBus.Publish(new RoadBuiltEvent(segment.Id, nodeA.Id, nodeB.Id, gameTime));
        }

        // ─────────────────────────────────────────────────────────
        //  Road demolition
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Removes a road segment. Nodes that no longer connect to anything are
        /// cleaned up automatically so orphaned intersection points do not linger.
        /// </summary>
        public bool DemolishRoad(int segmentId, float gameTime)
        {
            if (!Graph.Segments.TryGetValue(segmentId, out RoadSegment segment)) {
                return false;
            }

            int nodeAId = segment.NodeA;
            int nodeBId = segment.NodeB;

            Graph.RemoveSegment(segmentId);
            Graph.MarkDirty(nodeAId);
            Graph.MarkDirty(nodeBId);

            _eventBus.Publish(new RoadDemolishedEvent(segmentId, nodeAId, nodeBId, gameTime));

            // Remove nodes that no longer connect to anything
            if (Graph.Nodes.TryGetValue(nodeAId, out RoadNode nodeA) && nodeA.SegmentIds.Count == 0)
                Graph.RemoveNode(nodeAId);

            if (Graph.Nodes.TryGetValue(nodeBId, out RoadNode nodeB) && nodeB.SegmentIds.Count == 0)
                Graph.RemoveNode(nodeBId);

            return true;
        }

        // ─────────────────────────────────────────────────────────
        //  Node relocation
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Moves an existing node to a new world position.
        ///
        /// Publishes a RoadDemolishedEvent / RoadBuiltEvent pair for every segment
        /// that touches the node so the renderer, intersection builder, and any
        /// other listener automatically react without needing to know about node moves.
        ///
        /// Returns false when nodeId does not exist.
        /// </summary>
        public bool MoveNode(int nodeId, float3 newPosition, float gameTime)
        {
            if (!Graph.Nodes.TryGetValue(nodeId, out RoadNode node))
                return false;

            int[] affectedSegIds = node.SegmentIds.ToArray();

            foreach (int segId in affectedSegIds)
            {
                if (Graph.Segments.TryGetValue(segId, out RoadSegment seg))
                    _eventBus.Publish(new RoadDemolishedEvent(segId, seg.NodeA, seg.NodeB, gameTime));
            }

            Graph.MoveNode(nodeId, newPosition);
            Graph.MarkDirty(nodeId);

            foreach (int segId in affectedSegIds)
            {
                if (Graph.Segments.TryGetValue(segId, out RoadSegment seg))
                    _eventBus.Publish(new RoadBuiltEvent(segId, seg.NodeA, seg.NodeB, gameTime));
            }

            return true;
        }

        // ─────────────────────────────────────────────────────────
        //  Node resolution
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Finds or creates the node that a new road endpoint should connect to.
        ///
        /// Priority:
        ///   1. Nearest existing node within SnapRadius → reuse it (keeps graph clean)
        ///   2. Existing segment within SnapRadius, away from its endpoints
        ///      → split the segment to create a T-junction and return the new node
        ///   3. No nearby geometry → create a fresh isolated node
        ///
        /// When a split occurs, the old segment fires a DemolishedEvent and the two
        /// replacement halves fire BuiltEvents, keeping all listeners in sync.
        /// </summary>
        private RoadNode ResolveOrCreateNode(float3 position, float gameTime, int lanes, float speedLimit)
        {
            // Snap to existing node
            RoadNode? nearNode = Graph.FindNearestNode(position, SnapRadius);
            if (nearNode != null)
            {
                return nearNode;
            }

            (RoadSegment segment, float t)? hit = Graph.FindNearestSegment(position, SnapRadius);
            if (hit is not { t: > SplitEdgeGuard } || !(hit.Value.t < 1f - SplitEdgeGuard))
            {
                return Graph.AddNode(position);
            }

            int oldSegmentId = hit.Value.segment.Id;
            int oldNodeA = hit.Value.segment.NodeA;
            int oldNodeB = hit.Value.segment.NodeB;

            (RoadNode junctionNode, int newSegmentId) = Graph.SplitSegment(oldSegmentId, hit.Value.t);

            // Notify listeners: the original segment (old shape) is gone,
            // replaced by two halves with correct node IDs.
            _eventBus.Publish(new RoadDemolishedEvent(oldSegmentId, oldNodeA, oldNodeB, gameTime));

            // First half: the shortened original segment (same ID, new shape)
            if (Graph.Segments.TryGetValue(oldSegmentId, out RoadSegment firstHalf))
                _eventBus.Publish(new RoadBuiltEvent(firstHalf.Id, firstHalf.NodeA, firstHalf.NodeB, gameTime));

            // Second half: the newly created segment
            if (Graph.Segments.TryGetValue(newSegmentId, out RoadSegment secondHalf))
                _eventBus.Publish(new RoadBuiltEvent(secondHalf.Id, secondHalf.NodeA, secondHalf.NodeB, gameTime));

            Graph.MarkDirty(junctionNode.Id);
            return junctionNode;
        }
    }
}
