using Tsukimi;
using UnityEngine;

// The renderer and mesh are supplied by the consumer, independently of the live ball.
public partial class BallPhantomBehaviour : TsukimiBehaviour
{
    public Transform visual;
    public Renderer ballRenderer;
    public float meshDiameter = 1f;
    public Color strikeColor = new Color(1f, 1f, 0f, 0.5f);
    public Color ballColor = new Color(0f, 1f, 0f, 0.5f);
    public Color swingColor = new Color(1f, 0.5f, 0f, 0.5f);
    public bool visible;
    public bool swing;
    public bool strike;

    public void Start()
    {
        if (ballRenderer != null) ballRenderer.enabled = visible;
    }

    public void Show(Vector3 position, float radius, bool isStrike)
    {
        if (visual == null || ballRenderer == null || meshDiameter <= 0f) return;
        visual.position = position;
        float scale = 2f * radius / meshDiameter;
        visual.localScale = Vector3.one * scale;
        strike = isStrike;
        swing = false;
        visible = true;
        ballRenderer.material.color = strike ? strikeColor : ballColor;
        ballRenderer.enabled = true;
    }

    public void NotifySwing()
    {
        swing = true;
        if (visible && ballRenderer != null) ballRenderer.material.color = swingColor;
    }
}
