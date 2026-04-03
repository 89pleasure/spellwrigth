using Unity.Mathematics;

#nullable enable
namespace CityBuilder.Infrastructure.Roads
{
    /// <summary>
    /// Pure cubic Bézier mathematics for road curves.
    /// All methods are static and allocation-free (except BuildArcLengthLUT,
    /// which runs once at segment construction).
    ///
    /// A road segment's path is described by four points:
    ///   P0 = segment start  (NodeA world position)
    ///   P1 = ControlPointA  (first inner handle – pulls the road off the straight line)
    ///   P2 = ControlPointB  (second inner handle)
    ///   P3 = segment end    (NodeB world position)
    ///
    /// For a perfectly straight road, P1 and P2 sit at ⅓ and ⅔ along the straight
    /// line so the curve degenerates without any special-casing elsewhere.
    /// </summary>
    public static class BezierCurve
    {
        /// <summary>Number of samples stored in an arc-length LUT.</summary>
        public const int LutSamples = 128;

        // ─────────────────────────────────────────────────────────
        //  Curve evaluation
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// World position on the road curve at curve parameter t ∈ [0, 1].
        /// t = 0 is the start node; t = 1 is the end node.
        ///
        /// Uses de Casteljau's algorithm: three rounds of linear interpolation.
        /// Numerically stable and safe to call from Burst-compiled jobs.
        /// </summary>
        public static float3 Evaluate(float3 p0, float3 p1, float3 p2, float3 p3, float t)
        {
            float3 q0 = math.lerp(p0, p1, t);
            float3 q1 = math.lerp(p1, p2, t);
            float3 q2 = math.lerp(p2, p3, t);
            float3 r0 = math.lerp(q0, q1, t);
            float3 r1 = math.lerp(q1, q2, t);
            return math.lerp(r0, r1, t);
        }

        /// <summary>
        /// Normalized forward direction of the road at curve parameter t.
        /// Perpendicular to this is the road's right vector, used to orient
        /// the cross-section profile during mesh extrusion.
        ///
        /// Returns (0, 0, 1) when the tangent is zero-length (degenerate curve,
        /// e.g. start and end node at the same position). This fallback points
        /// along +Z, which may cause visual artifacts on vertical roads – ensure
        /// roads have non-zero length before calling this method.
        /// </summary>
        public static float3 EvaluateTangent(float3 p0, float3 p1, float3 p2, float3 p3, float t)
        {
            float u = 1f - t;
            float3 tangent =
                3f * u * u * (p1 - p0) +
                6f * u * t * (p2 - p1) +
                3f * t * t * (p3 - p2);
            return math.normalizesafe(tangent, new float3(0f, 0f, 1f));
        }

        // ─────────────────────────────────────────────────────────
        //  Curve splitting
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Splits the curve at parameter t into two cubic Bézier halves using
        /// de Casteljau subdivision. Returns only the inner control handles;
        /// the shared split point is Evaluate(p0, p1, p2, p3, t).
        ///
        ///   Left  half [0 … t]: endpoints are  (p0,         leftP1,  leftP2,  splitPoint)
        ///   Right half [t … 1]: endpoints are  (splitPoint, rightP1, rightP2, p3)
        ///
        /// Called when the player draws a new road that meets an existing segment
        /// at its middle, creating a T-junction. The existing segment must be
        /// divided into two shorter segments at that crossing point.
        /// </summary>
        public static void SplitAt(
            float3 p0, float3 p1, float3 p2, float3 p3, float t,
            out float3 leftP1,  out float3 leftP2,
            out float3 rightP1, out float3 rightP2)
        {
            float3 q0 = math.lerp(p0, p1, t);
            float3 q1 = math.lerp(p1, p2, t);
            float3 q2 = math.lerp(p2, p3, t);
            float3 r0 = math.lerp(q0, q1, t);
            float3 r1 = math.lerp(q1, q2, t);

            leftP1  = q0;   // Left half:  P0 → q0 → r0 → split
            leftP2  = r0;
            rightP1 = r1;   // Right half: split → r1 → q2 → P3
            rightP2 = q2;
        }

        // ─────────────────────────────────────────────────────────
        //  Arc-length parameterization
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a lookup table mapping curve parameter t to real-world distance
        /// along the road. lut[0] = 0 m; lut[127] = total road length in metres.
        ///
        /// Why this is needed: Bézier curves are not naturally uniform in speed –
        /// equal steps of t produce unequal distances along the road. Without the
        /// LUT, mesh cross-sections, road markings, and parcel placements all cluster
        /// near the curve's ends and spread out in the middle.
        ///
        /// Allocates once at segment construction – not called per frame.
        /// </summary>
        public static float[] BuildArcLengthLUT(float3 p0, float3 p1, float3 p2, float3 p3)
        {
            float[] lut      = new float[LutSamples];
            float3  previous = p0;
            lut[0] = 0f;

            for (int i = 1; i < LutSamples; i++)
            {
                float  t       = i / (float)(LutSamples - 1);
                float3 current = Evaluate(p0, p1, p2, p3, t);
                lut[i]   = lut[i - 1] + math.distance(previous, current);
                previous = current;
            }

            return lut;
        }

        /// <summary>
        /// Converts a curve parameter t to the real-world arc-length distance (metres)
        /// by interpolating the LUT. Inverse of ArcLengthToT.
        ///
        /// Example use: computing how many metres along the road a trim point sits
        /// so the mesh builder can sample the visible range in uniform arc-length steps.
        /// </summary>
        public static float TToArcLength(float[] lut, float t)
        {
            if (t <= 0f) return 0f;
            float totalLength = lut[lut.Length - 1];
            if (t >= 1f) return totalLength;

            float fIndex = t * (lut.Length - 1);
            int   lo     = (int)fIndex;
            int   hi     = lo + 1;
            if (hi >= lut.Length) return totalLength;

            float fraction = fIndex - lo;
            return math.lerp(lut[lo], lut[hi], fraction);
        }

        /// <summary>
        /// Converts a real-world arc-length distance s (metres) to the corresponding
        /// curve parameter t, using binary search on the LUT + linear interpolation.
        ///
        /// Example use: "place a bus stop every 50 m along this road"
        ///   → s = 50, 100, 150 … → ArcLengthToT gives the t values
        ///   → Evaluate(t) gives the world positions, evenly spaced.
        /// </summary>
        public static float ArcLengthToT(float[] lut, float s)
        {
            float totalLength = lut[lut.Length - 1];
            if (s <= 0f)          return 0f;
            if (s >= totalLength) return 1f;

            int lo = 0;
            int hi = lut.Length - 1;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (lut[mid] < s) lo = mid;
                else              hi = mid;
            }

            float segmentLength = lut[hi] - lut[lo];
            float fraction      = segmentLength > 0f ? (s - lut[lo]) / segmentLength : 0f;
            float tLo           = lo / (float)(lut.Length - 1);
            float tHi           = hi / (float)(lut.Length - 1);
            return math.lerp(tLo, tHi, fraction);
        }
    }
}
