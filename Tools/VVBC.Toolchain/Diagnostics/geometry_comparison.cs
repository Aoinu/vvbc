var go = new UnityEngine.GameObject("vvbc-temp-geometry-test");
try {
 var motion=go.AddComponent<BatMotionBehaviour>(); var feedback=go.AddComponent<BattingFeedbackBehaviour>();
 var body=go.AddComponent<UnityEngine.Rigidbody>();
 var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
 typeof(BatMotionBehaviour).GetField("body",flags).SetValue(motion,body);
 var type=typeof(BattingFeedbackBehaviour);
 var fx=type.GetField("_candidateX",flags); var fy=type.GetField("_candidateYValue",flags);
 var fw=type.GetField("_candidateWidth",flags); var fr=type.GetField("_candidateBallRadius",flags);
 var fb=type.GetField("_candidateBall",flags); var fc=type.GetField("_candidateCore",flags); var fn=type.GetField("_candidateNormal",flags);
 var ready=type.GetField("_outlineReady",flags); var ends=type.GetField("_outlineEndsReady",flags);
 var calc=type.GetMethod("CandidateDistance",flags);
 var curves=new UnityEngine.AnimationCurve[] {
  UnityEngine.AnimationCurve.Linear(0,.0084f,1,.0334f),
  UnityEngine.AnimationCurve.Linear(0,.002f,1,.05f),
  new UnityEngine.AnimationCurve(new UnityEngine.Keyframe(0,.005f),new UnityEngine.Keyframe(.55f,.05f),new UnityEngine.Keyframe(1,.012f)),
  UnityEngine.AnimationCurve.Linear(0,.00001f,1,.0001f),
  new UnityEngine.AnimationCurve(new UnityEngine.Keyframe(0,.001f),new UnityEngine.Keyframe(.49f,.001f),new UnityEngine.Keyframe(.5f,.08f),new UnityEngine.Keyframe(.51f,.001f),new UnityEngine.Keyframe(1,.001f)) };
 var scales=new UnityEngine.Vector3[] { UnityEngine.Vector3.one,new UnityEngine.Vector3(2f,1.7f,.4f),new UnityEngine.Vector3(.45f,.7f,1.3f) };
 System.Func<UnityEngine.Vector2,UnityEngine.Vector2,UnityEngine.Vector2,float> segment=(p,a,b)=>{float dx=b.x-a.x,dy=b.y-a.y,px=p.x-a.x,py=p.y-a.y,len=dx*dx+dy*dy,t=len>0?UnityEngine.Mathf.Clamp01((px*dx+py*dy)/len):0;float rx=px-t*dx,ry=py-t*dy;return rx*rx+ry*ry;};
 float maxError=0,maxWidthError=0;int count=0,mismatch=0,insideMismatch=0;
 foreach(var curve in curves) foreach(var scale in scales) foreach(float margin in new float[]{0f,.012f}) foreach(float radius in new float[]{0f,.0366f}) {
  motion.batLength=.855f;motion.radiusCurve=curve;motion.sweetSpotNormalized=.7f;motion.contactStartNormalized=.117f;motion.contactEndNormalized=1f;motion.contactMargin=margin;motion.minimumQuality=.25f;
  go.transform.localScale=scale; ready.SetValue(feedback,false);ends.SetValue(feedback,false); motion.PrepareFeedbackGeometry(radius);
  float start=motion.ContactStartLocalY(),end=motion.ContactEndLocalY();float min=motion.PlaneDistanceFromLocalY(start),max=motion.PlaneDistanceFromLocalY(end);
  for(int ix=-2;ix<=22;ix++)for(int iy=-10;iy<=10;iy++) {
   float x=UnityEngine.Mathf.LerpUnclamped(min,max,ix/20f),y=iy*.0125f;
   float clamped=UnityEngine.Mathf.Clamp(motion.ContactPlaneDistanceToLocalY(x),start,end);
   float width=motion.ContactWidthAtLocalY(clamped,radius); float fastWidth=motion.FeedbackSampleWidth(x); maxWidthError=UnityEngine.Mathf.Max(maxWidthError,UnityEngine.Mathf.Abs(width-fastWidth)); if(!(width>0))continue;
   float minw=motion.ContactWidthAtLocalY(start,radius),maxw=motion.ContactWidthAtLocalY(end,radius); var point=new UnityEngine.Vector2(x,y);float planar=0;
   bool outside=x<min||x>max||UnityEngine.Mathf.Abs(y)>width;
   if(outside) {
    planar=UnityEngine.Mathf.Min(segment(point,new UnityEngine.Vector2(min,-minw),new UnityEngine.Vector2(min,minw)),segment(point,new UnityEngine.Vector2(max,maxw),new UnityEngine.Vector2(max,-maxw)));
    float px=min,pw=minw;for(int i=1;i<=16;i++){float nx=UnityEngine.Mathf.Lerp(min,max,i/16f);float nw=motion.ContactWidthAtLocalY(motion.ContactPlaneDistanceToLocalY(nx),radius);planar=UnityEngine.Mathf.Min(planar,UnityEngine.Mathf.Min(segment(point,new UnityEngine.Vector2(px,-pw),new UnityEngine.Vector2(nx,-nw)),segment(point,new UnityEngine.Vector2(px,pw),new UnityEngine.Vector2(nx,nw))));px=nx;pw=nw;}
   }
   float expected=UnityEngine.Mathf.Sqrt(planar+.0025f); fx.SetValue(feedback,x);fy.SetValue(feedback,y);fw.SetValue(feedback,width);fr.SetValue(feedback,radius);fb.SetValue(feedback,new UnityEngine.Vector3(0,0,.05f));fc.SetValue(feedback,UnityEngine.Vector3.zero);fn.SetValue(feedback,UnityEngine.Vector3.forward);
   float actual=(float)calc.Invoke(feedback,new object[]{motion});float error=UnityEngine.Mathf.Abs(expected-actual);if(error>maxError)maxError=error;if(error>.0001f||float.IsNaN(actual))mismatch++;if(outside != (x<min||x>max||UnityEngine.Mathf.Abs(y)>fastWidth))insideMismatch++;count++;
  }
 }
 return $"samples={count} maxError={maxError:R} maxWidthError={maxWidthError:R} mismatches={mismatch} boundaryMismatch={insideMismatch}";
} finally { UnityEngine.Object.DestroyImmediate(go); }
