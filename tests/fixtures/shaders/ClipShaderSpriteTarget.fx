
#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif
sampler s0;// i was not able to use this register TODO investigate in mg 3.8.1, so i add two parameters.. to access params was differnet between GL and DX before, also might have been fixed.    It might save memory to use this on big textures.    theres are other samples of multitexutuer shaders in the folder but none are tested... i collected old ones and got them to build... feel free to test fix them if generally useful on DX and Gl and android.


Texture2D ClipTexture;

sampler ClipTextureSampler = sampler_state
{
    Texture = <ClipTexture>;
};



Texture2D DrawTexture;

sampler DrawTexSampler = sampler_state
{
    Texture = <DrawTexture>;
};


//TODO clipping could mabye be done at this level but it seems cheaper at the pixel level.. I chose a render target and pixel mask, render polygons to it, then can clip to any shape or texture.    

//it can work with mipmaps. for zoomable  2d images and billboards  they resample nicely zooming out... antialiasis on GL is regressed, colors are too light  .. This is worked about by shutting of multisampling ..  This makes rasterizing polygons to be ckipped not so great, they will have stair case edges.. But you can maybe so some aliasing on the clip result texture to smooth edges.. as yhou render to the screen wiht the scaling effect.

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};


float4 MainPS(VertexShaderOutput input) : COLOR
{
       

 // float4 color = tex2D(s0, input.TextureCoordinates);t   //didnt work on al platfroms on 3.8, REVISIT TODO

    float4 mask = tex2D(ClipTextureSampler, input.TextureCoordinates);

    float4 color = tex2D(DrawTexSampler, input.TextureCoordinates);
    

  if ( all(mask) == all(float4(1, 1, 1, 1))) //there are  built in stencis, and other clipping stuff in Basic effect but I couldnt get them to work.  the samples are from XNA times
  {
   return color;  //use anything as a mask..
  }
  else {

   //return float4(1, 0, 1, 0.7);  //this is for testing touch in the  fx by uncommenting this, and build,  works on dx and gl.. and adroid.. you will see a different result.  blending is still tricky.    but holes are ok and 100% parallel.  this effectively creates a single tranparent texture like a PNG at runtime.     the background will show though the channel and anthing drawn before.
  return float4(0, 1, 1, 0);//    transparent.     
  }

 
  
}


technique Technique1
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
    
}




 