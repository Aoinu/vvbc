using TMPro;
using UnityEngine;

// Pose feedback (local to the pitcher, no collider, nothing synced): a protractor about the
// finger axis where the release's twist was judged. The ideal palm (a thin line in one fixed
// colour) and the measured palm (a thick line coloured by the pose quality) point from the
// hand, the good range is an arc around the ideal, the bad limits a wider arc inside it, and a
// label gives the numbers. Kept until the next hold, like the hand trail. A diagnostic mode draws
// the same live for the selected type, with the controller's own axes, every frame while on.
// See docs/development/manual-pitching.md.
public partial class ManualPitchBehaviour
{
    [Header("Pose feedback (local)")]
    [Tooltip("Show the ideal and the measured twist of the last release as a protractor about the finger axis, with the numbers. Needs showReleaseFeedback. Default on.")]
    public bool showPoseFeedback = true;
    [Tooltip("Diagnostics: every frame in pitcher mode, the live twist protractor of the selected type with its numbers, and the controller's X (red), Y (green), Z (blue) axes. Default off.")]
    public bool showLivePalm;
    [Tooltip("Length of the palm lines and radius of the arcs, in metres. Default 0.25.")]
    public float poseArrowLength = 0.25f;
    [Tooltip("Width of the measured palm line, in metres; the ideal line and the arcs are thinner. Default 0.008.")]
    public float poseArrowWidth = 0.008f;
    [Tooltip("Font size of the twist numbers next to the protractor (TextMeshPro). Default 0.5.")]
    public float poseLabelFontSize = 0.5f;
    [Tooltip("Ideal palm line: one fixed colour, never a quality colour.")]
    public Color idealPalmColor = new Color(0.2f, 0.85f, 1f, 1f);
    [Tooltip("Lines of a type whose twist is not evaluated.")]
    public Color unevaluatedPalmColor = new Color(0.6f, 0.6f, 0.6f, 0.7f);
    [Tooltip("Measured palm at full quality; it turns to badPalmColor through the warning colour as the quality falls.")]
    public Color goodPalmColor = new Color(0.2f, 1f, 0.3f, 1f);
    public Color warnPalmColor = new Color(1f, 0.9f, 0.1f, 1f);
    public Color badPalmColor = new Color(1f, 0.2f, 0.15f, 1f);
    [Tooltip("Arc of the good range (full quality) around the ideal.")]
    public Color goodArcColor = new Color(0.2f, 1f, 0.3f, 0.45f);
    [Tooltip("Arc up to the bad angle (no quality left) around the ideal.")]
    public Color badArcColor = new Color(1f, 0.25f, 0.2f, 0.45f);
    [Tooltip("Ideal palm line. When empty, the child Feedback/IdealPalm is used.")]
    public LineRenderer idealPalmLine;
    [Tooltip("Measured palm line. When empty, the child Feedback/ActualPalm is used.")]
    public LineRenderer actualPalmLine;
    [Tooltip("Arc of the good range. When empty, the child Feedback/GoodAngleRing is used.")]
    public LineRenderer goodAngleRing;
    [Tooltip("Arc up to the bad angle. When empty, the child Feedback/BadAngleRing is used.")]
    public LineRenderer badAngleRing;
    [Tooltip("Twist numbers of the last release. When empty, the child Feedback/PoseLabel is used.")]
    public TextMeshPro poseLabel;
    [Tooltip("Live measured palm (diagnostics). When empty, the child Feedback/LivePalm is used.")]
    public LineRenderer livePalmLine;
    [Tooltip("Live ideal palm of the selected type (diagnostics). When empty, the child Feedback/LiveIdealPalm is used.")]
    public LineRenderer liveIdealLine;
    [Tooltip("Live controller X, Y and Z axes (diagnostics). When empty, the children Feedback/LiveAxisX, LiveAxisY and LiveAxisZ are used.")]
    public LineRenderer liveAxisX;
    public LineRenderer liveAxisY;
    public LineRenderer liveAxisZ;
    [Tooltip("Live twist numbers (diagnostics). When empty, the child Feedback/LivePoseLabel is used.")]
    public TextMeshPro livePoseLabel;

    private const int ArcPoints = 24;
    private readonly Vector3[] _arcPoints = new Vector3[ArcPoints];
    private readonly Vector3[] _arrowPoints = new Vector3[2];
    // Where and in which pitching frame the last release's pose was judged.
    private bool _resultPoseValid;
    private Vector3 _resultPoseOrigin;
    private Quaternion _resultPoseFrame = Quaternion.identity;

    private void FindPoseFeedbackChildren()
    {
        if (idealPalmLine == null) idealPalmLine = FeedbackLine("Feedback/IdealPalm");
        if (actualPalmLine == null) actualPalmLine = FeedbackLine("Feedback/ActualPalm");
        if (goodAngleRing == null) goodAngleRing = FeedbackLine("Feedback/GoodAngleRing");
        if (badAngleRing == null) badAngleRing = FeedbackLine("Feedback/BadAngleRing");
        if (livePalmLine == null) livePalmLine = FeedbackLine("Feedback/LivePalm");
        if (liveIdealLine == null) liveIdealLine = FeedbackLine("Feedback/LiveIdealPalm");
        if (liveAxisX == null) liveAxisX = FeedbackLine("Feedback/LiveAxisX");
        if (liveAxisY == null) liveAxisY = FeedbackLine("Feedback/LiveAxisY");
        if (liveAxisZ == null) liveAxisZ = FeedbackLine("Feedback/LiveAxisZ");
        if (poseLabel == null) poseLabel = FeedbackText("Feedback/PoseLabel");
        if (livePoseLabel == null) livePoseLabel = FeedbackText("Feedback/LivePoseLabel");
    }

    private LineRenderer FeedbackLine(string path)
    {
        Transform child = transform.Find(path);
        return child != null ? child.GetComponent<LineRenderer>() : null;
    }

    private TextMeshPro FeedbackText(string path)
    {
        Transform child = transform.Find(path);
        return child != null ? child.GetComponent<TextMeshPro>() : null;
    }

    // ---- The judged twist of the last release ----

    // The measured palm's colour: good at quality 1, warning at 0.5, bad at 0.
    public Color QualityColor(float quality)
    {
        float q = ManualPitchMath.Finite(quality) ? Mathf.Clamp01(quality) : 0f;
        return q >= 0.5f ? Color.Lerp(warnPalmColor, goodPalmColor, (q - 0.5f) * 2f) : Color.Lerp(badPalmColor, warnPalmColor, q * 2f);
    }

    // The lines' end points of the last release (world): ideal and measured palm from the origin.
    public Vector3 IdealPalmTip()
    {
        return _resultPoseOrigin + ManualPitchMath.Rotate(_resultPoseFrame, _resultQuality.idealPalm) * ArrowLength();
    }

    public Vector3 ActualPalmTip()
    {
        return _resultPoseOrigin + ManualPitchMath.Rotate(_resultPoseFrame, _resultQuality.actualPalm) * ArrowLength();
    }

    public Vector3 PoseOrigin() { return _resultPoseOrigin; }
    public bool PoseFeedbackShown() { return showReleaseFeedback && showPoseFeedback && _resultPitched && _resultPoseValid; }

    private float ArrowLength()
    {
        return ManualPitchMath.Finite(poseArrowLength) ? Mathf.Max(0.01f, poseArrowLength) : 0.25f;
    }

    // A point of the arc about the finger axis at the twist `twistDegrees` (pronation positive),
    // `radius` x the line length from the origin, in world space.
    public Vector3 ArcPoint(Vector3 origin, Quaternion frame, PitchQuality q, float twistDegrees, float radius)
    {
        Vector3 local = ManualPitchQuality.PalmAtTwist(q.fingerAxis, q.referencePalm, twistDegrees, _activeHand == GloveBehaviour.HandLeft);
        return origin + ManualPitchMath.Rotate(frame, local) * (ArrowLength() * radius);
    }

    // The label text: ideal, measured, error, pose quality (ASCII: the label font has no Japanese).
    public string PoseLabelText(PitchQuality q)
    {
        if (!q.poseEvaluated) return "twist not evaluated" + (q.twistDefined ? "\nActual " + TwistLabel(q.actualTwist) : "");
        if (!q.twistDefined) return "twist not measured\n(fingers at the plate)";
        return "Ideal " + TwistLabel(q.idealTwist) + "\nActual " + TwistLabel(q.actualTwist) +
            "\nError " + q.poseAngle.ToString("F0") + "°  Pose " + Percent(q.poseQuality);
    }

    // Drawn when the release is decided; the next hold, the end of pitcher mode and calibration
    // clear it (ClearReleaseFeedback).
    private void ShowPoseFeedback()
    {
        if (!PoseFeedbackShown())
        {
            HidePoseFeedback();
            return;
        }
        DrawProtractor(_resultPoseOrigin, _resultPoseFrame, _resultQuality, idealPalmLine, actualPalmLine, goodAngleRing,
            badAngleRing, poseLabel);
    }

    private void HidePoseFeedback()
    {
        ClearLine(idealPalmLine);
        ClearLine(actualPalmLine);
        ClearLine(goodAngleRing);
        ClearLine(badAngleRing);
        ShowLabel(poseLabel, false, Vector3.zero, "");
    }

    // The ideal (thin, fixed colour) and the measured palm (thick, quality colour), the good arc
    // around the ideal at full radius and the bad arc at 0.8 of it, and the numbers.
    private void DrawProtractor(Vector3 origin, Quaternion frame, PitchQuality q, LineRenderer ideal, LineRenderer actual,
        LineRenderer good, LineRenderer bad, TextMeshPro label)
    {
        float length = ArrowLength();
        Color measured = q.poseEvaluated ? QualityColor(q.poseQuality) : unevaluatedPalmColor;
        if (q.actualPalm != Vector3.zero)
            DrawLine(actual, origin, origin + ManualPitchMath.Rotate(frame, q.actualPalm) * length, measured, LineWidth());
        else ClearLine(actual);
        bool arcs = q.poseEvaluated && q.twistDefined;
        if (q.twistDefined)
            DrawLine(ideal, origin, origin + ManualPitchMath.Rotate(frame, q.idealPalm) * length,
                q.poseEvaluated ? idealPalmColor : unevaluatedPalmColor, LineWidth() * 0.5f);
        else ClearLine(ideal);
        DrawArc(good, arcs, origin, frame, q, q.goodAngle, 1f, goodArcColor);
        DrawArc(bad, arcs, origin, frame, q, q.badAngle, 0.8f, badArcColor);
        ShowLabel(label, true, origin + ManualPitchMath.Rotate(frame, q.fingerAxis) * (length * 0.35f), PoseLabelText(q));
    }

    // The arc from ideal - degrees to ideal + degrees about the finger axis.
    private void DrawArc(LineRenderer line, bool shown, Vector3 origin, Quaternion frame, PitchQuality q, float degrees, float radius, Color color)
    {
        if (line == null) return;
        if (!shown)
        {
            line.positionCount = 0;
            return;
        }
        float half = Mathf.Clamp(ManualPitchMath.Finite(degrees) ? degrees : 0f, 0f, 180f);
        for (int i = 0; i < ArcPoints; i++)
            _arcPoints[i] = ArcPoint(origin, frame, q, q.idealTwist - half + 2f * half * i / (ArcPoints - 1), radius);
        line.loop = false;
        line.widthMultiplier = LineWidth() * 0.4f;
        line.startColor = color;
        line.endColor = color;
        line.positionCount = ArcPoints;
        line.SetPositions(_arcPoints);
    }

    private void DrawLine(LineRenderer line, Vector3 from, Vector3 to, Color color, float width)
    {
        if (line == null) return;
        _arrowPoints[0] = from;
        _arrowPoints[1] = to;
        line.widthMultiplier = width;
        line.startColor = color;
        line.endColor = color;
        line.positionCount = 2;
        line.SetPositions(_arrowPoints);
    }

    // The label faces the pitcher (TextMeshPro reads along -Z), like the type label.
    private void ShowLabel(TextMeshPro label, bool shown, Vector3 position, string text)
    {
        if (label == null) return;
        if (shown)
        {
            label.transform.SetPositionAndRotation(position, Quaternion.LookRotation(_plate.valid ? _plate.normal : Vector3.forward, Vector3.up));
            label.fontSize = Mathf.Max(0.01f, poseLabelFontSize);
            label.text = text;
        }
        if (label.gameObject.activeSelf != shown) label.gameObject.SetActive(shown);
    }

    private float LineWidth()
    {
        return ManualPitchMath.Finite(poseArrowWidth) ? Mathf.Max(0.001f, poseArrowWidth) : 0.008f;
    }

    private static void ClearLine(LineRenderer line)
    {
        if (line != null && line.positionCount != 0) line.positionCount = 0;
    }

    // ---- Live twist (diagnostics) ----

    // Every frame while showLivePalm: the selected type's protractor for the current hand and
    // the controller's own axes, to take a pose or to read which controller axis is the palm.
    private void ShowLivePalm(Vector3 position, Quaternion rotation)
    {
        if (!showLivePalm || state == StateInactive || strikeZone == null)
        {
            // Off: nothing per frame, only one clean-up after the last live frame.
            if (_livePalmShown) HideLivePalm();
            return;
        }
        _livePalmShown = true;
        Quaternion frame = ManualPitchMath.Frame(ManualPitchMath.HorizontalDirection(Vector3.zero,
            -StrikeZoneGeometry.Forward(strikeZone.PlateTransform().forward)));
        PitchQuality q = EvaluateQuality(_selectedType, rotation, frame, ReleasePlateMath.TimingIdeal);
        DrawProtractor(position, frame, q, liveIdealLine, livePalmLine, null, null, livePoseLabel);
        float axisLength = ArrowLength() * 0.4f;
        float width = LineWidth() * 0.5f;
        DrawLine(liveAxisX, position, position + ManualPitchMath.Rotate(rotation, Vector3.right) * axisLength, Color.red, width);
        DrawLine(liveAxisY, position, position + ManualPitchMath.Rotate(rotation, Vector3.up) * axisLength, Color.green, width);
        DrawLine(liveAxisZ, position, position + ManualPitchMath.Rotate(rotation, Vector3.forward) * axisLength, Color.blue, width);
    }

    private bool _livePalmShown;

    private void HideLivePalm()
    {
        _livePalmShown = false;
        ClearLine(livePalmLine);
        ClearLine(liveIdealLine);
        ClearLine(liveAxisX);
        ClearLine(liveAxisY);
        ClearLine(liveAxisZ);
        ShowLabel(livePoseLabel, false, Vector3.zero, "");
    }
}
