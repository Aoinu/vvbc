var provider=new Microsoft.CSharp.CSharpCodeProvider();var options=new System.CodeDom.Compiler.CompilerParameters();options.GenerateInMemory=true;
var seen=new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
foreach(var assembly in System.AppDomain.CurrentDomain.GetAssemblies()) { try { string location=assembly.Location; if(!string.IsNullOrEmpty(location)&&seen.Add(location)) options.ReferencedAssemblies.Add(location); } catch {} }
string baselineRoot="D:/Workspaces/VVBC/diag/58b5c0a/Packages/org.vvbc.core/Runtime/";
var names=new[]{"BattingFeedbackBehaviour.cs","BattingFeedbackBehaviour.Scan.cs","BattingFeedbackBehaviour.Trail.cs"};var sources=new string[names.Length];
for(int i=0;i<names.Length;i++)sources[i]=System.IO.File.ReadAllText(baselineRoot+names[i]).Replace("BattingFeedbackBehaviour","VVBCBaselineFeedback");
var result=provider.CompileAssemblyFromSource(options,sources);
var errors=new System.Collections.Generic.List<string>();foreach(System.CodeDom.Compiler.CompilerError e in result.Errors)if(!e.IsWarning)errors.Add(e.ToString());
if(errors.Count>0)return "baseline compiler errors: "+string.Join(" | ",errors);
var baselineType=result.CompiledAssembly.GetType("VVBCBaselineFeedback");

var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
var roots=new System.Collections.Generic.List<UnityEngine.GameObject>();
try {
 int checks=0, failures=0, abMismatches=0, contactChecks=0, lifecycleChecks=0; float maxTimeDiff=0f,maxFloatDiff=0f,maxPositionDiff=0f; var outputs=new System.Collections.Generic.List<string>();
 foreach (int scenario in new int[]{0,1,2,3,4,5,6,7,8}) {
  var bat=new UnityEngine.GameObject("vvbc-temp-scan-bat"); var ball=new UnityEngine.GameObject("vvbc-temp-scan-ball"); var lane=new UnityEngine.GameObject("vvbc-temp-scan-lane"); roots.Add(bat);roots.Add(ball);roots.Add(lane);
  var rb=bat.AddComponent<UnityEngine.Rigidbody>(); var motion=bat.AddComponent<BatMotionBehaviour>();
  typeof(BatMotionBehaviour).GetField("body",flags).SetValue(motion,rb);
  motion.batLength=.855f;motion.radiusCurve=scenario==1 || scenario==3 ? null : UnityEngine.AnimationCurve.Linear(0,.0084f,1,.0334f);if(scenario==4 || scenario==5) { motion.radiusCurve=new UnityEngine.AnimationCurve(new UnityEngine.Keyframe(0,.004f),new UnityEngine.Keyframe(.55f,.055f),new UnityEngine.Keyframe(1,.014f));bat.transform.localScale=new UnityEngine.Vector3(2f,.7f,1.3f);motion.contactMargin=.01f; } if(scenario==6)motion.radiusCurve=UnityEngine.AnimationCurve.Linear(0,.002f,1,.05f); if(scenario==7)motion.radiusCurve=new UnityEngine.AnimationCurve(new UnityEngine.Keyframe(0,.001f),new UnityEngine.Keyframe(.49f,.001f),new UnityEngine.Keyframe(.5f,.08f),new UnityEngine.Keyframe(1,.001f)); if(scenario==8)motion.radiusCurve=UnityEngine.AnimationCurve.Linear(0,.00001f,1,.0001f);motion.sweetSpotNormalized=.7f;motion.contactStartNormalized=.117f;motion.contactEndNormalized=1f;motion.minimumQuality=.25f;
  var mt=typeof(BatMotionBehaviour); ((float[])mt.GetField("_contactTimes",flags).GetValue(motion))[0]=1f;((float[])mt.GetField("_contactTimes",flags).GetValue(motion))[1]=2f;((float[])mt.GetField("_contactTimes",flags).GetValue(motion))[2]=3f;
  if(scenario>=4) {var poses=(UnityEngine.Vector3[])mt.GetField("_contactPositions",flags).GetValue(motion);var velocities=(UnityEngine.Vector3[])mt.GetField("_contactVelocities",flags).GetValue(motion);poses[0]=UnityEngine.Vector3.left*.3f;poses[1]=UnityEngine.Vector3.zero;poses[2]=UnityEngine.Vector3.right*.3f;velocities[0]=velocities[1]=velocities[2]=UnityEngine.Vector3.right*.3f;}
  var rotations=(UnityEngine.Quaternion[])mt.GetField("_contactRotations",flags).GetValue(motion);rotations[0]=rotations[1]=rotations[2]=UnityEngine.Quaternion.identity;
  if(scenario==6) {rotations[0]=UnityEngine.Quaternion.Euler(0f,0f,-30f);rotations[2]=UnityEngine.Quaternion.Euler(0f,0f,30f);} if(scenario==7) {rotations[0]=UnityEngine.Quaternion.Euler(25f,0f,-15f);rotations[2]=UnityEngine.Quaternion.Euler(-20f,0f,20f);}
  var refvel=(UnityEngine.Vector3[])mt.GetField("_contactReferenceVelocities",flags).GetValue(motion);refvel[0]=refvel[1]=refvel[2]=UnityEngine.Vector3.right*6f;
  mt.GetField("_contactCount",flags).SetValue(motion,3);mt.GetField("_contactNext",flags).SetValue(motion,3);motion.contactOldestTime=1f;motion.contactLatestTime=3f;
  var col=ball.AddComponent<UnityEngine.SphereCollider>();col.radius=.0366f;var flight=ball.AddComponent<BallBehaviour>();flight.ballCollider=col;
  var record=typeof(BallBehaviour).GetMethod("RecordMotion",flags);var v=UnityEngine.Vector3.forward;for(int i=0;i<3;i++)record.Invoke(flight,new object[]{1f+i,new UnityEngine.Vector3(0,.5f,-1f+i),v,UnityEngine.Vector3.zero});
  var contact=lane.AddComponent<BatContactBehaviour>();contact.contactZone=lane.transform;contact.zoneHalfSize=(scenario==2||scenario==3||scenario==5)?new UnityEngine.Vector3(1f,1f,.01f):new UnityEngine.Vector3(1f,1f,.5f); if(scenario==2||scenario==3||scenario==5)lane.transform.position=UnityEngine.Vector3.forward*100f;
  var feedback=lane.AddComponent<BattingFeedbackBehaviour>();feedback.contact=contact;feedback.flight=flight;
  var displayA=new UnityEngine.GameObject("feedback-display");displayA.transform.SetParent(lane.transform); feedback.metrics=displayA.AddComponent<UnityEngine.UI.Text>();
  typeof(BattingFeedbackBehaviour).GetMethod("Scan",flags).Invoke(feedback,new object[]{motion});
  var old=lane.AddComponent(baselineType); baselineType.GetField("contact").SetValue(old,contact); baselineType.GetField("flight").SetValue(old,flight);var displayB=new UnityEngine.GameObject("baseline-display");displayB.transform.SetParent(lane.transform);var textB=displayB.AddComponent<UnityEngine.UI.Text>();baselineType.GetField("metrics").SetValue(old,textB); baselineType.GetMethod("Scan",flags).Invoke(old,new object[]{motion});
  typeof(BattingFeedbackBehaviour).GetMethod("UpdateMetrics",flags).Invoke(feedback,null);baselineType.GetMethod("UpdateMetrics",flags).Invoke(old,null);if(feedback.metrics.text!=textB.text){abMismatches++;outputs.Add($"scenario={scenario} metrics differ");}
  var namesToCompare=new[]{"_entered","_exited","_hasClosest","_hasResult","_isContact","_shown","_ballRotationValid","_hasPreviousCandidateY","_scanTime","_entryTime","_exitTime","_time","_distance","_speed","_angular","_rawPower","_powerCap","_power","_yaw","_planeX","_planeY","_normalizedY","_peakSpeed","_peakAngular","_quality","_capReason","_batPosition","_ballPosition","_ballRotation","_batRotation","_previousCandidateY","_resultVelocity","_resultSpin"};
  foreach(var fieldName in namesToCompare) {
   object a=typeof(BattingFeedbackBehaviour).GetField(fieldName,flags).GetValue(feedback),b=baselineType.GetField(fieldName,flags).GetValue(old); bool same=true;
   if(a is float) { float delta=UnityEngine.Mathf.Abs((float)a-(float)b); if(fieldName=="_time")maxTimeDiff=UnityEngine.Mathf.Max(maxTimeDiff,delta);else maxFloatDiff=UnityEngine.Mathf.Max(maxFloatDiff,delta); same=delta<=(fieldName=="_time"?.000001f:.0001f); }
   else if(a is UnityEngine.Vector3) {float delta=((UnityEngine.Vector3)a-(UnityEngine.Vector3)b).magnitude;maxPositionDiff=UnityEngine.Mathf.Max(maxPositionDiff,delta);same=delta<=.0001f;}
   else if(a is UnityEngine.Quaternion) {var qa=(UnityEngine.Quaternion)a;var qb=(UnityEngine.Quaternion)b;same=UnityEngine.Mathf.Abs(qa.x-qb.x)<.0001f&&UnityEngine.Mathf.Abs(qa.y-qb.y)<.0001f&&UnityEngine.Mathf.Abs(qa.z-qb.z)<.0001f&&UnityEngine.Mathf.Abs(qa.w-qb.w)<.0001f;}
   else same=object.Equals(a,b);
   if(!same) { abMismatches++; outputs.Add($"scenario={scenario} field={fieldName} actual={a} baseline={b}"); }
  }
  var ft=typeof(BattingFeedbackBehaviour);bool entered=(bool)ft.GetField("_entered",flags).GetValue(feedback),exited=(bool)ft.GetField("_exited",flags).GetValue(feedback),closest=(bool)ft.GetField("_hasClosest",flags).GetValue(feedback),previous=(bool)ft.GetField("_hasPreviousCandidateY",flags).GetValue(feedback);
  bool ok=scenario==0 ? entered&&exited&&closest : scenario==1 ? !entered&&!exited&&!closest&&!previous : (scenario==2||scenario==5) ? !entered&&!exited&&!closest&&previous : scenario==3 ? !entered&&!exited&&!closest&&!previous : entered&&exited&&closest;
  checks++;if(!ok)failures++;outputs.Add($"{scenario}:entry={entered},exit={exited},closest={closest},previous={previous}");
  if(scenario==4) {
   int revision=motion.FeedbackGeometryRevision();
   for(int stage=0;stage<=12;stage++) {
    if(stage==1){var key=motion.radiusCurve[1];key.value+=.01f;motion.radiusCurve.MoveKey(1,key);}
    else if(stage==2)motion.batLength=.95f;
    else if(stage==3)motion.contactStartNormalized=.2f;
    else if(stage==4)motion.contactEndNormalized=.9f;
    else if(stage==5)motion.contactMargin=.02f;
    else if(stage==6)bat.transform.localScale=new UnityEngine.Vector3(1.3f,.9f,.4f);
    else if(stage==7)col.radius=.05f;
    else if(stage==8){if(!motion.SetBatGeometry(.9f,UnityEngine.AnimationCurve.Linear(0,.02f,1,.04f),.5f,.1f,.8f,.01f,.25f)){failures++;outputs.Add("SetBatGeometry failed");}}
    else if(stage==9)motion.ResetBattingSettings();
    else if(stage==10){bat.SetActive(false);bat.SetActive(true);}
    else if(stage==11){motion.batLength=-1f;var key=motion.radiusCurve[1];key.value+=.02f;motion.radiusCurve.MoveKey(1,key);}
    else if(stage==12)motion.batLength=.95f;
    // Set/Reset intentionally clear measurement history when the reference
    // point changes. Restore the same synthetic samples for the next Scan.
    if(stage==8||stage==9){mt.GetField("_contactCount",flags).SetValue(motion,3);mt.GetField("_contactNext",flags).SetValue(motion,3);motion.contactOldestTime=1f;motion.contactLatestTime=3f;}
    typeof(BattingFeedbackBehaviour).GetMethod("ResetForSequence",flags).Invoke(feedback,new object[]{stage+1});baselineType.GetMethod("ResetForSequence",flags).Invoke(old,new object[]{stage+1});
    typeof(BattingFeedbackBehaviour).GetMethod("Scan",flags).Invoke(feedback,new object[]{motion});baselineType.GetMethod("Scan",flags).Invoke(old,new object[]{motion});
    int currentRevision=motion.FeedbackGeometryRevision();bool expectedChange=stage!=0&&stage!=10&&stage!=11;
    if((currentRevision!=revision)!=expectedChange){abMismatches++;outputs.Add($"lifecycle stage={stage} unexpected revision {revision}->{currentRevision}");}revision=currentRevision;
    foreach(var fieldName in namesToCompare){object a=typeof(BattingFeedbackBehaviour).GetField(fieldName,flags).GetValue(feedback),b=baselineType.GetField(fieldName,flags).GetValue(old);bool same=object.Equals(a,b);if(a is float) same=UnityEngine.Mathf.Abs((float)a-(float)b)<=(fieldName=="_time"?.000001f:.0001f);else if(a is UnityEngine.Vector3) same=((UnityEngine.Vector3)a-(UnityEngine.Vector3)b).magnitude<=.0001f;if(!same){abMismatches++;outputs.Add($"lifecycle stage={stage} field={fieldName} actual={a} baseline={b}");}}
    typeof(BattingFeedbackBehaviour).GetMethod("UpdateMetrics",flags).Invoke(feedback,null);baselineType.GetMethod("UpdateMetrics",flags).Invoke(old,null);if(feedback.metrics.text!=textB.text){abMismatches++;outputs.Add($"lifecycle stage={stage} metrics differ");}lifecycleChecks++;
   }
  }
  if(scenario==0 || scenario==4 || scenario==6) {
   contactChecks++;contact.contactValid=true;contact.contactPitchSequence=flight.flightSequence;contact.contactTime=2f;contact.contactPosition=new UnityEngine.Vector3(.1f,.5f,.02f);contact.contactQuality=.73f;contact.finalBatPower=.81f;
   contact.snapshotBatRootPosition=new UnityEngine.Vector3(.2f,.1f,.3f);contact.snapshotBatRotation=UnityEngine.Quaternion.Euler(0f,20f,0f);contact.snapshotBallRotation=UnityEngine.Quaternion.Euler(10f,20f,30f);contact.snapshotBallRotationValid=true;
   contact.snapshotReferenceSpeed=12f;contact.snapshotBatAngularVelocity=new UnityEngine.Vector3(1f,2f,3f);contact.snapshotRawPower=.9f;contact.snapshotPowerCap=.85f;contact.snapshotPowerCapReason=1;
   contact.snapshotPlaneX=.55f;contact.snapshotPlaneY=.02f;contact.snapshotNormalizedY=.4f;contact.snapshotX=UnityEngine.Vector3.right;contact.resultVelocity=new UnityEngine.Vector3(10f,5f,20f);contact.resultSpin=new UnityEngine.Vector3(0f,8f,0f);
   typeof(BattingFeedbackBehaviour).GetMethod("OnContactConfirmed").Invoke(feedback,null);baselineType.GetMethod("OnContactConfirmed").Invoke(old,null);
   foreach(var fieldName in namesToCompare) { object a=typeof(BattingFeedbackBehaviour).GetField(fieldName,flags).GetValue(feedback),b=baselineType.GetField(fieldName,flags).GetValue(old); if(!object.Equals(a,b)){abMismatches++;outputs.Add($"contact scenario={scenario} field={fieldName} actual={a} baseline={b}");} }
   typeof(BattingFeedbackBehaviour).GetMethod("UpdateMetrics",flags).Invoke(feedback,null);baselineType.GetMethod("UpdateMetrics",flags).Invoke(old,null);if(feedback.metrics.text!=textB.text){abMismatches++;outputs.Add($"contact scenario={scenario} metrics differ");}
  }
 }
 return $"checks={checks} failures={failures} abMismatches={abMismatches} contactChecks={contactChecks} lifecycleChecks={lifecycleChecks} maxTimeDiff={maxTimeDiff:R} maxFloatDiff={maxFloatDiff:R} maxPositionDiff={maxPositionDiff:R} " + string.Join("; ",outputs);
} finally { foreach(var root in roots) UnityEngine.Object.DestroyImmediate(root); }
