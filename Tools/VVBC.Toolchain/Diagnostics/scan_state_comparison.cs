var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
var roots=new System.Collections.Generic.List<UnityEngine.GameObject>();
try {
 int checks=0, failures=0; var outputs=new System.Collections.Generic.List<string>();
 foreach (int scenario in new int[]{0,1,2,3}) {
  var bat=new UnityEngine.GameObject("vvbc-temp-scan-bat"); var ball=new UnityEngine.GameObject("vvbc-temp-scan-ball"); var lane=new UnityEngine.GameObject("vvbc-temp-scan-lane"); roots.Add(bat);roots.Add(ball);roots.Add(lane);
  var rb=bat.AddComponent<UnityEngine.Rigidbody>(); var motion=bat.AddComponent<BatMotionBehaviour>();
  typeof(BatMotionBehaviour).GetField("body",flags).SetValue(motion,rb);
  motion.batLength=.855f;motion.radiusCurve=scenario==1 || scenario==3 ? null : UnityEngine.AnimationCurve.Linear(0,.0084f,1,.0334f);motion.sweetSpotNormalized=.7f;motion.contactStartNormalized=.117f;motion.contactEndNormalized=1f;motion.minimumQuality=.25f;
  var mt=typeof(BatMotionBehaviour); ((float[])mt.GetField("_contactTimes",flags).GetValue(motion))[0]=1f;((float[])mt.GetField("_contactTimes",flags).GetValue(motion))[1]=2f;((float[])mt.GetField("_contactTimes",flags).GetValue(motion))[2]=3f;
  var rotations=(UnityEngine.Quaternion[])mt.GetField("_contactRotations",flags).GetValue(motion);rotations[0]=rotations[1]=rotations[2]=UnityEngine.Quaternion.identity;
  var refvel=(UnityEngine.Vector3[])mt.GetField("_contactReferenceVelocities",flags).GetValue(motion);refvel[0]=refvel[1]=refvel[2]=UnityEngine.Vector3.right*6f;
  mt.GetField("_contactCount",flags).SetValue(motion,3);mt.GetField("_contactNext",flags).SetValue(motion,3);motion.contactOldestTime=1f;motion.contactLatestTime=3f;
  var col=ball.AddComponent<UnityEngine.SphereCollider>();col.radius=.0366f;var flight=ball.AddComponent<BallBehaviour>();flight.ballCollider=col;
  var record=typeof(BallBehaviour).GetMethod("RecordMotion",flags);var v=UnityEngine.Vector3.forward;for(int i=0;i<3;i++)record.Invoke(flight,new object[]{1f+i,new UnityEngine.Vector3(0,.5f,-1f+i),v,UnityEngine.Vector3.zero});
  var contact=lane.AddComponent<BatContactBehaviour>();contact.contactZone=lane.transform;contact.zoneHalfSize=scenario>=2?new UnityEngine.Vector3(1f,1f,.01f):new UnityEngine.Vector3(1f,1f,.5f); if(scenario>=2)lane.transform.position=UnityEngine.Vector3.forward*100f;
  var feedback=lane.AddComponent<BattingFeedbackBehaviour>();feedback.contact=contact;feedback.flight=flight;
  typeof(BattingFeedbackBehaviour).GetMethod("Scan",flags).Invoke(feedback,new object[]{motion});
  var ft=typeof(BattingFeedbackBehaviour);bool entered=(bool)ft.GetField("_entered",flags).GetValue(feedback),exited=(bool)ft.GetField("_exited",flags).GetValue(feedback),closest=(bool)ft.GetField("_hasClosest",flags).GetValue(feedback),previous=(bool)ft.GetField("_hasPreviousCandidateY",flags).GetValue(feedback);
  bool ok=scenario==0 ? entered&&exited&&closest : scenario==1 ? !entered&&!exited&&!closest&&!previous : scenario==2 ? !entered&&!exited&&!closest&&previous : !entered&&!exited&&!closest&&!previous;
  checks++;if(!ok)failures++;outputs.Add($"{scenario}:entry={entered},exit={exited},closest={closest},previous={previous}");
 }
 return $"checks={checks} failures={failures} " + string.Join("; ",outputs);
} finally { foreach(var root in roots) UnityEngine.Object.DestroyImmediate(root); }
