
using UnityEngine;
using System;
using System.Collections;
using System.Runtime.InteropServices;
using OpenNI;

public class KinectMesh : MonoBehaviour
{
    public float zThreshold = 0.0f;
    public bool applyBlur = true;
    public int blurIterations = 1;

    public bool applyLerp = true;
    public float lerpSpeed = 1.0f;
    
    public Vector3 gridScale = Vector3.one;
    public bool GenerateNormals = false;
    public bool GenerateUVs = true;
	//can't find a counterpart to this in the new ZDK
    //public bool RealWorldPoints = true; // perform perspective transform on depth-map

    public Vector2 DesiredResolution = new Vector2(160, 120); // should be a divisor of 640x480
                                                              // and 320x240 is too high (too many vertices)
	
	public float interval = 2f; // seconds
	float nextUpdateTime = 0;
	
    int factorX;
    int factorY;

    short[] rawDepthMap;
    float[] depthHistogramMap;
    int XRes;
    int YRes;
    Mesh curMesh;
    MeshFilter meshFilter;

    Vector2[] uvs;
    Vector3[] verts;
    int[] tris;
    Point3D[] pts;

    MeshBlur meshBlur;
    MeshLerp meshLerp;
    
    // Use this for initialization
    void Start()
    {
		nextUpdateTime = Time.time + interval;
		
        // init stuff
        YRes = ZigInput.Depth.yres;
        XRes = ZigInput.Depth.xres;
        factorX = (int)(XRes / DesiredResolution.x);
        factorY = (int)(YRes / DesiredResolution.y);
        // depthmap data
        rawDepthMap = new short[(int)(XRes * YRes)];

        // the actual mesh we'll use
        
        curMesh = new Mesh();

        meshFilter = (MeshFilter)GetComponent(typeof(MeshFilter));
        meshFilter.mesh = curMesh;

        int YScaled = YRes / factorY;
        int XScaled = XRes / factorX;

        verts = new Vector3[XScaled * YScaled];
        uvs = new Vector2[verts.Length];
        tris = new int[(XScaled - 1) * (YScaled - 1) * 2 * 3];
        pts = new Point3D[XScaled * YScaled];
        CalculateTriangleIndices(YScaled, XScaled);
        CalculateUVs(YScaled, XScaled);

		// Gaussian blur
		meshBlur = new MeshBlur();
		meshBlur.myFilter = new int[,] {{1, 2, 1}, {2, 4, 2}, {1, 2, 1}};
	
		// Mesh lerp
		meshLerp = new MeshLerp(XScaled, YScaled);
    }

    void UpdateDepthmapMesh(Mesh mesh)
    {
        if (meshFilter == null)
            return;
        Profiler.BeginSample("UpdateDepthmapMesh");
        mesh.Clear();
        
        // flip the depthmap as we create the texture
        int YScaled = YRes / factorY;
        int XScaled = XRes / factorX;
        // first stab, generate all vertices (next time, only vertices for 'valid' depths)
        // first stab, decimate rather than average depth pixels
        UpdateVertices(mesh, YScaled, XScaled);
        if (GenerateUVs) {
            UpdateUVs(mesh, YScaled, XScaled);
        }
        UpdateTriangleIndices(mesh);
        // normals - if we generate we need to update them according to the new mesh
        if (GenerateNormals) {
            mesh.RecalculateNormals();
        }

		GetComponent<MeshCollider>().sharedMesh = null;
		GetComponent<MeshCollider>().sharedMesh = mesh;

        Profiler.EndSample();
    }

    private void UpdateUVs(Mesh mesh, int YScaled, int XScaled)
    {
        Profiler.BeginSample("UpdateUVs");
        mesh.uv = uvs;
        Profiler.EndSample();
    }

    private void CalculateUVs(int YScaled, int XScaled)
    {
        for (int y = 0; y < YScaled; y++) {
            for (int x = 0; x < XScaled; x++) {
                //uvs[y * XScaled + x] = new Vector2((float)x / (float)XScaled,
                //                       (float)y / (float)YScaled);
                uvs[y * XScaled + x].x = (float)x / (float)XScaled;
                uvs[y * XScaled + x].y = ((float)(YScaled - 1 - y) / (float)YScaled);
            }
        }
    }
    
    private void UpdateVertices(Mesh mesh, int YScaled, int XScaled)
    {
        int depthIndex = 0;
        Profiler.BeginSample("UpdateVertices");

        Profiler.BeginSample("FillPoint3Ds");
        //DepthGenerator dg = OpenNIContext.Instance.Depth;
        //short maxDepth = (short)OpenNIContext.Instance.Depth.DeviceMaxDepth;
		short maxDepth = (short) 2^11 - 1;
        Vector3 vec = new Vector3();
        Point3D pt = new Point3D();
        for (int y = 0; y < YScaled; y++) {
            for (int x = 0; x < XScaled; x++, depthIndex += factorX) {
                short pixel = rawDepthMap[depthIndex];
                if (pixel == 0) pixel = maxDepth; // if there's no depth,  default to max depth

                // RW coordinates
                pt.X = x * factorX;
                pt.Y = y * factorY;
		if (pixel >= zThreshold) {
		    pt.Z = pixel;
		}
                pts[x + y * XScaled] = pt; // in structs, assignment is a copy, so modifying the same variable
                                           // every iteration is okay
            }
            // Skip lines
            depthIndex += (factorY - 1) * XRes;
        }
        Profiler.EndSample();
        Profiler.BeginSample("ProjectiveToRW");

        for (int i = 0; i < pts.Length; i++) {
            pts[i].X -= XRes / 2;
             pts[i].Y = (YRes / 2) - pts[i].Y; // flip Y axis in projective
        }
        Profiler.EndSample();
        Profiler.BeginSample("PointsToVertices");
        for (int y = 0; y < YScaled; y++) {
            for (int x = 0; x < XScaled; x++) {
                pt = pts[x + y * XScaled];
                vec.x = pt.X * gridScale.x;
                vec.y = pt.Y * gridScale.y;
                vec.z = -pt.Z * gridScale.z;
                verts[y * XScaled + x] = vec;
            }
        }
        Profiler.EndSample();
        Profiler.BeginSample("AssignVerticesToMesh");
	if (this.applyBlur) {
	    for (int i = 0; i < blurIterations; i++) {
		meshBlur.applyFilter(verts, XScaled, YScaled);
	    }
	}
	if (this.applyLerp) {
	    meshLerp.speed = this.lerpSpeed;
	    meshLerp.applyLerp(verts);
	}
        mesh.vertices = verts;
        Profiler.EndSample();

        Profiler.EndSample();
    }

    private void UpdateTriangleIndices(Mesh mesh)
    {
        Profiler.BeginSample("UpdateTriangleIndices");

        mesh.triangles = tris;
        Profiler.EndSample();
    }

    private void CalculateTriangleIndices(int YScaled, int XScaled)
    {
        int triIndex = 0;
        int posIndex = 0;
        for (int y = 0; y < (YScaled - 1); y++) {
            for (int x = 0; x < (XScaled - 1); x++, posIndex++) {
                // Counter-clockwise triangles

                tris[triIndex++] = posIndex + 1; // bottom right
                tris[triIndex++] = posIndex; // bottom left
                tris[triIndex++] = posIndex + XScaled; // top left

                tris[triIndex++] = posIndex + 1; // bottom right
                tris[triIndex++] = posIndex + XScaled; // top left
                tris[triIndex++] = posIndex + XScaled + 1; // top right
            }
            posIndex++; // finish row
        }
    }

    void FixedUpdate()
    {		
		if (Time.time > nextUpdateTime) {
		    nextUpdateTime = Time.time + interval;

			rawDepthMap = ZigInput.Depth.data;
			UpdateDepthmapMesh(curMesh);
		}
	}
}