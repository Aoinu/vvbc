// Which manual pitch a ball state comes from: the pitcher's player ID and pitch number (the
// pitch ID), the prepared-data generation it was released against and the batter it was
// prepared for. A HandPitch starts a lineage; a hit, catch or throw keeps the lineage of the
// flight it came from; a Stage, a batting-pitcher pitch or a recall starts none (all zero).
public struct PitchLineage
{
    public int pitcher;
    public int number;
    public int prep;
    public int batter;

    public bool IsPitch()
    {
        return pitcher > 0 && number > 0;
    }

    public bool SamePitch(int otherPitcher, int otherNumber)
    {
        return pitcher > 0 && pitcher == otherPitcher && number == otherNumber;
    }
}
