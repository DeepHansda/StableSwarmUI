﻿using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using StableSwarmUI.Accounts;
using StableSwarmUI.Utils;
using ISImage = SixLabors.ImageSharp.Image;
using Image = StableSwarmUI.Utils.Image;
using SixLabors.ImageSharp.PixelFormats;
using System;

namespace StableSwarmUI.Text2Image;

/// <summary>This special utility class generates prompts that have multi-step object generation included.
/// Called by <see cref="T2IEngine"/>, should not be called directly.</summary>
public class T2IMultiStepObjectBuilder
{
    public static async Task<Image> CreateFullImage(string prompt, T2IParamInput user_input, string batchId, Session.GenClaim claim, Action<JObject> output, Action<string> setError, bool isWS, float backendTimeoutMin)
    {
        int obj = 0;
        async Task<Image> createImageDirect(T2IParamInput user_input)
        {
            Image result = null;
            await T2IEngine.CreateImageTask(user_input, batchId + (obj++), claim, output, setError, isWS, backendTimeoutMin, (img, meta) => { result = img; }, false);
            return result;
        }
        if (string.IsNullOrWhiteSpace(prompt) || !prompt.Contains("<object:"))
        {
            return null;
        }
        PromptRegion regions = new(prompt);
        PromptRegion.Part[] objects = regions.Parts.Where(p => p.Type == PromptRegion.PartType.Object).ToArray();
        if (objects.IsEmpty())
        {
            return null;
        }
        user_input = user_input.Clone();
        if (user_input.TryGet(T2IParamTypes.AltResolutionHeightMult, out _))
        {
            user_input.Set(T2IParamTypes.Height, user_input.GetImageHeight());
            user_input.Remove(T2IParamTypes.AspectRatio);
            user_input.Remove(T2IParamTypes.AltResolutionHeightMult);
        }
        user_input.Remove(T2IParamTypes.RefinerModel);
        user_input.Remove(T2IParamTypes.RefinerUpscale);
        user_input.Remove(T2IParamTypes.RefinerMethod);
        user_input.Set(T2IParamTypes.EndStepsEarly, 0.2);
        user_input.Set(T2IParamTypes.Seed, user_input.Get(T2IParamTypes.Seed) + 1);
        claim.Extend(1 + objects.Length);
        Image img = await createImageDirect(user_input);
        if (img is null)
        {
            return null;
        }
        //user_input.Set(T2IParamTypes.EndStepsEarly, 0.6); // TODO: Configurable
        using ISImage liveImg = img.ToIS;
        float overBound = 0.3f;
        foreach (PromptRegion.Part part in objects)
        {
            user_input.Set(T2IParamTypes.Seed, user_input.Get(T2IParamTypes.Seed) + 1);
            T2IParamInput objInput = user_input.Clone();
            objInput.Set(T2IParamTypes.Prompt, part.Prompt);
            int pixelX = (int)(part.X * liveImg.Width);
            int pixelY = (int)(part.Y * liveImg.Height);
            int pixelWidth = (int)(part.Width * liveImg.Width);
            int pixelHeight = (int)(part.Height * liveImg.Height);
            int extraX = Math.Max((int)((part.X - overBound * 0.5) * liveImg.Width), 0);
            int extraY = Math.Max((int)((part.Y - overBound * 0.5) * liveImg.Height), 0);
            int extraWidth = Math.Min((int)((part.Width + overBound) * liveImg.Width), liveImg.Width - extraX);
            int extraHeight = Math.Min((int)((part.Height + overBound) * liveImg.Height), liveImg.Height - extraY);
            (int fixedWidth, int fixedHeight) = Utilities.ResToModelFit(extraWidth, extraHeight, liveImg.Width * liveImg.Height);
            using ISImage subImage = liveImg.Clone(i => i.Crop(new Rectangle(extraX, extraY, extraWidth, extraHeight)).Resize(fixedWidth, fixedHeight));
            int borderWidth = (int)(0.075 * fixedWidth);
            int borderHeight = (int)(0.075 * fixedHeight);
            using ISImage maskImage = ISImage.LoadPixelData<Rgb24>(Enumerable.Repeat(Color.Black.ToPixel<Rgb24>(), 1).ToArray(), 1, 1);
            using ISImage maskInnerImage = ISImage.LoadPixelData<Rgb24>(Enumerable.Repeat(Color.White.ToPixel<Rgb24>(), 1).ToArray(), 1, 1);
            maskInnerImage.Mutate(i => i.Resize(fixedWidth - borderWidth, fixedHeight - borderHeight));
            maskImage.Mutate(i => i.Resize(fixedWidth, fixedHeight).DrawImage(maskInnerImage, new Point(borderWidth / 2, borderHeight / 2), 1));
            objInput.Set(T2IParamTypes.MaskImage, new(maskImage));
            objInput.Set(T2IParamTypes.Prompt, part.Prompt);
            objInput.Set(T2IParamTypes.InitImage, new Image(subImage));
            objInput.Set(T2IParamTypes.InitImageCreativity, part.Strength2);
            objInput.Set(T2IParamTypes.Width, fixedWidth);
            objInput.Set(T2IParamTypes.Height, fixedHeight);
            objInput.Remove(T2IParamTypes.ControlNetModel);
            Image objImg = await createImageDirect(objInput);
            if (objImg is null)
            {
                return null;
            }
            using ISImage objISImg = objImg.ToIS;
            objISImg.Mutate(i => i.Resize(extraWidth, extraHeight));
            liveImg.Mutate(i => i.DrawImage(objISImg, new Point(extraX, extraY), 1));
            output(new JObject() { ["image"] = "data:image/png;base64," + new Image(liveImg).AsBase64, ["batch_index"] = $"{batchId}{obj++}", ["metadata"] = null });
        }
        return new(liveImg);
    }
}