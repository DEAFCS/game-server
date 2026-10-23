using FiveStack.Entities.Practice;

namespace FiveStack.Utilities;

// Which spawn point E puts a player on (DEAFCS).
//
// The same rules as snapping onto a saved lineup, so the key behaves one way:
// what the crosshair points at wins over what is nearest, either one has to be
// within reach, and standing on it already does nothing. It is only ever asked
// after the lineup snap found nothing, so a saved lineup next to a spawn always
// wins -- the spawn is the fallback, never the competitor.
public static class PracticeSpawnSnap
{
    // The lineup snap's own reach (UseReachUnits): near enough that use plainly
    // means "put me there", bounded so picking up a weapon cannot cross a room.
    public const float ReachUnits = 220f;

    // The lineup hover cone (RingHoverDegrees), and its floor: a spot straight
    // under the eye is never what "looking at" means.
    public const double PointDegrees = 7.0;
    public const float MinPointDistance = 96f;

    public const float EyeHeight = 64f;

    /// <summary>
    /// The index of the spawn to stand on, or -1 for none. <paramref name="visible" />
    /// answers whether a point is in view from the eye; a spawn pointed at
    /// through a wall is not one the player can see they are choosing.
    /// </summary>
    public static int Choose(
        Vec3 standing,
        float eyeYaw,
        float eyePitch,
        IReadOnlyList<ThrowSnapshot> spawns,
        Func<Vec3, Vec3, bool>? visible = null
    )
    {
        if (spawns.Count == 0)
        {
            return -1;
        }

        var eye = new Vec3(standing.x, standing.y, standing.z + EyeHeight);
        Vec3 view = View(eyeYaw, eyePitch);

        int pointed = -1;
        double bestAngle = PointDegrees;
        int nearest = -1;
        float bestAway = float.MaxValue;

        for (int index = 0; index < spawns.Count; index++)
        {
            Vec3 feet = spawns[index].feet_position;
            float away = new Vec3(feet.x - standing.x, feet.y - standing.y, 0f).LengthXY();

            if (away > ReachUnits)
            {
                continue;
            }

            if (away < bestAway)
            {
                bestAway = away;
                nearest = index;
            }

            var toSpawn = new Vec3(feet.x - eye.x, feet.y - eye.y, feet.z - eye.z);
            float length = toSpawn.Length();

            if (length < MinPointDistance)
            {
                continue;
            }

            double dot = (view.x * toSpawn.x + view.y * toSpawn.y + view.z * toSpawn.z) / length;
            double angle = Math.Acos(Math.Clamp(dot, -1.0, 1.0)) * 180.0 / Math.PI;

            if (angle < bestAngle && (visible == null || visible(eye, feet)))
            {
                bestAngle = angle;
                pointed = index;
            }
        }

        int chosen = pointed >= 0 ? pointed : nearest;

        if (chosen < 0)
        {
            return -1;
        }

        // Already on it: teleporting somebody onto the spot they stand on reads
        // as the key doing nothing and costs them their run-up.
        Vec3 target = spawns[chosen].feet_position;
        float off = new Vec3(target.x - standing.x, target.y - standing.y, 0f).LengthXY();

        return off <= PracticeLineupUtility.StanceToleranceUnits ? -1 : chosen;
    }

    // CS2 angles: yaw around Z, pitch positive looking down.
    public static Vec3 View(float yawDegrees, float pitchDegrees)
    {
        double yaw = yawDegrees * Math.PI / 180.0;
        double pitch = pitchDegrees * Math.PI / 180.0;
        double flat = Math.Cos(pitch);

        return new Vec3(
            (float)(Math.Cos(yaw) * flat),
            (float)(Math.Sin(yaw) * flat),
            (float)(-Math.Sin(pitch))
        );
    }
}
