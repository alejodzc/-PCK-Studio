﻿/* Copyright (c) 2024-present miku-666
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 * 
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 * 
 * 1.The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would be
 *    appreciated but is not required.
 * 2. Altered source versions must be plainly marked as such, and must not be
 *    misrepresented as being the original software.
 * 3. This notice may not be removed or altered from any source distribution.
**/
using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Numerics;
using System.Diagnostics;
using System.Windows.Forms;
using System.Drawing.Imaging;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using PckStudio.Extensions;
using PckStudio.Internal.Skin;
using PckStudio.Internal.IO.PSM;
using PckStudio.External.Format;
using PckStudio.Internal.FileFormats;
using PckStudio.Forms.Additional_Popups;

namespace PckStudio.Internal
{
    internal sealed class SkinModelImporter : ModelImporter<SkinModelInfo>
    {
        public static SkinModelImporter Default { get; } = new SkinModelImporter();

        private SkinModelImporter()
        {
            InternalAddProvider(new("Pck skin model(*.psm)", "*.psm"), ImportPsm, ExportPsm);
            InternalAddProvider(new("Block bench model(*.bbmodel)", "*.bbmodel"), ImportBlockBenchModel, ExportBlockBenchModel);
            InternalAddProvider(new("Bedrock (Legacy) Model(*.geo.json;*.json)", "*.geo.json;*.json"), ImportBedrockJson, ExportBedrockJson);
        }

        internal static SkinModelInfo ImportPsm(string filepath)
        {
            var reader = new PSMFileReader();
            PSMFile csmbFile = reader.FromFile(filepath);
            return new SkinModelInfo(null, csmbFile.SkinANIM, csmbFile.Parts, csmbFile.Offsets);
        }

        internal static void ExportPsm(string filepath, SkinModelInfo modelInfo)
        {
            PSMFile psmFile = new PSMFile(PSMFile.CurrentVersion, modelInfo.ANIM);
            psmFile.Parts.AddRange(modelInfo.AdditionalBoxes);
            psmFile.Offsets.AddRange(modelInfo.PartOffsets);
            var writer = new PSMFileWriter(psmFile);
            writer.WriteToFile(filepath);
        }

        internal static SkinModelInfo ImportBlockBenchModel(string filepath)
        {
            BlockBenchModel blockBenchModel = JsonConvert.DeserializeObject<BlockBenchModel>(File.ReadAllText(filepath));
            if (!blockBenchModel.Format.UseBoxUv)
            {
                Trace.TraceError($"[{nameof(SkinModelImporter)}:{nameof(ImportBlockBenchModel)}] Failed to import skin '{blockBenchModel.Name}': Skin does not use box uv.");
                return null;
            }

            IEnumerable<SkinPartOffset> partOffsets = blockBenchModel.Outliner
                .Where(token => token.Type == JTokenType.Object && SkinBOX.IsValidType(TryConvertToSkinBoxType(token.ToObject<Outline>().Name)))
                .Select(token => token.ToObject<Outline>())
                .Select(outline => new SkinPartOffset(TryConvertToSkinBoxType(outline.Name), -GetOffsetFromOrigin(TryConvertToSkinBoxType(outline.Name), outline.Origin).Y))
                .Where(offset => offset.Value != 0f);

            IEnumerable<SkinBOX> boxes = ReadOutliner(null, blockBenchModel.Outliner, blockBenchModel.Elements);

            SkinModelInfo modelInfo = CreateSkinModelInfo(boxes, partOffsets);

            if (blockBenchModel.Textures.IndexInRange(0))
            {
                modelInfo.Texture = blockBenchModel.Textures[0];
                modelInfo.Texture = SwapBoxBottomTexture(modelInfo);
                modelInfo.ANIM = modelInfo.ANIM.SetFlag(SkinAnimFlag.RESOLUTION_64x64, modelInfo.Texture.Size.Width == modelInfo.Texture.Size.Height);
            }

            return modelInfo;
        }

        private static SkinModelInfo CreateSkinModelInfo(IEnumerable<SkinBOX> boxes, IEnumerable<SkinPartOffset> partOffsets)
        {
            SkinModelInfo modelInfo = new SkinModelInfo();
            modelInfo.ANIM = (
                   SkinAnimMask.HEAD_DISABLED |
                   SkinAnimMask.HEAD_OVERLAY_DISABLED |
                   SkinAnimMask.BODY_DISABLED |
                   SkinAnimMask.BODY_OVERLAY_DISABLED |
                   SkinAnimMask.RIGHT_ARM_DISABLED |
                   SkinAnimMask.RIGHT_ARM_OVERLAY_DISABLED |
                   SkinAnimMask.LEFT_ARM_DISABLED |
                   SkinAnimMask.LEFT_ARM_OVERLAY_DISABLED |
                   SkinAnimMask.RIGHT_LEG_DISABLED |
                   SkinAnimMask.RIGHT_LEG_OVERLAY_DISABLED |
                   SkinAnimMask.LEFT_LEG_DISABLED |
                   SkinAnimMask.LEFT_LEG_OVERLAY_DISABLED);

            modelInfo.PartOffsets.AddRange(partOffsets);

            SkinBOX ApplyOffset(SkinBOX box)
            {
                SkinPartOffset offset = modelInfo.PartOffsets.FirstOrDefault(offset => offset.Type == (box.IsOverlayPart() ? box.GetBaseType() : box.Type));
                return string.IsNullOrEmpty(offset.Type) ? box : new SkinBOX(box.Type, box.Pos - (Vector3.UnitY * offset.Value), box.Size, box.UV, box.HideWithArmor, box.Mirror, box.Scale);
            }

            IEnumerable<SkinBOX> convertedBoxes = boxes.Select(ApplyOffset);

            IEnumerable<SkinBOX> customBoxes = convertedBoxes.Where(box => !SkinBOX.KnownHashes.ContainsKey(box.GetHashCode()));

            modelInfo.AdditionalBoxes.AddRange(customBoxes);

            // check for know boxes and filter them out
            SkinAnimMask mask = (SkinAnimMask)convertedBoxes
                .Where(box => SkinBOX.KnownHashes.ContainsKey(box.GetHashCode()) && Enum.IsDefined(typeof(SkinAnimMask), (1 >> (int)SkinBOX.KnownHashes[box.GetHashCode()])))
                .Select(box => SkinBOX.KnownHashes[box.GetHashCode()])
                .Select(i => 1 << (int)i)
                .DefaultIfEmpty()
                .Aggregate((a, b) => a | b);

            if (mask != SkinAnimMask.NONE)
                modelInfo.ANIM &= ~mask;
            
            return modelInfo;
        }

        private static IEnumerable<SkinBOX> ReadOutliner(string parentName, JArray oulineChildren, IReadOnlyCollection<Element> elements)
        {
            IEnumerable<SkinBOX> boxes = oulineChildren
                .Where(token => token.Type == JTokenType.String && Guid.TryParse(token.ToString(), out Guid elementUuid) && elements.Any(e => e.Uuid == elementUuid))
                .Select(token => elements.First(e => Guid.Parse(token.ToString()) == e.Uuid))
                .Where(element => element.Type == "cube" && element.UseBoxUv && element.Export && SkinBOX.IsValidType(TryConvertToSkinBoxType(parentName ?? element.Name)))
                .Select(element => LoadElement(element, TryConvertToSkinBoxType(parentName ?? element.Name)));

            IEnumerable<Outline> childOutlines = oulineChildren
                .Where(token => token.Type == JTokenType.Object)
                .Select(token => token.ToObject<Outline>());

            foreach (Outline childOutline in childOutlines)
            {
                boxes = boxes.Concat(ReadOutliner(parentName ?? childOutline.Name, childOutline.Children, elements));
            }
            return boxes;
        }

        private static SkinBOX LoadElement(Element element, string outlineName)
        {
            var boundingBox = new Rendering.BoundingBox(element.From, element.To);
            Vector3 pos = boundingBox.Start;
            Vector3 size = boundingBox.Volume;
            Vector2 uv = element.UvOffset;

            pos = TranslateToInternalPosition(outlineName, pos, size, new Vector3(1, 1, 0));

            var box = new SkinBOX(outlineName, pos, size, uv, mirror: element.MirrorUv);
            if (box.IsBasePart() && ((outlineName == "HEAD" && element.Inflate == 0.5f) || (element.Inflate >= 0.25f && element.Inflate <= 0.5f)))
                box.Type = box.GetOverlayType();
            return box;
        }

        internal static void ExportBlockBenchModel(string filepath, SkinModelInfo modelInfo)
        {
            Image exportTexture = SwapBoxBottomTexture(modelInfo);
            BlockBenchModel blockBenchModel = BlockBenchModel.Create(BlockBenchFormatInfos.BedrockEntity, Path.GetFileNameWithoutExtension(filepath), new Size(64, exportTexture.Width == exportTexture.Height ? 64 : 32), [exportTexture]);

            Dictionary<string, Outline> outliners = new Dictionary<string, Outline>(5);
            List<Element> elements = new List<Element>(modelInfo.AdditionalBoxes.Count);

            Dictionary<string, SkinPartOffset> offsetLookUp = new Dictionary<string, SkinPartOffset>(5);

            void AddElement(SkinBOX box)
            {
                string offsetType = box.IsOverlayPart() ? box.GetBaseType() : box.Type;

                Vector3 offset = GetOffsetForPart(offsetType, ref offsetLookUp, modelInfo.PartOffsets);
                if (!outliners.ContainsKey(offsetType))
                {
                    outliners.Add(offsetType, new Outline(offsetType)
                    {
                        Origin = GetSkinPartPivot(offsetType, new Vector3(1, 1, 0)) + offset
                    });
                }

                Element element = CreateElement(box);

                element.From += offset;
                element.To += offset;

                elements.Add(element);
                outliners[offsetType].Children.Add(element.Uuid);
            }

            ANIM2BOX(modelInfo.ANIM, AddElement);

            foreach (SkinBOX box in modelInfo.AdditionalBoxes)
            {
                AddElement(box);
            }
            blockBenchModel.Elements = elements.ToArray();
            blockBenchModel.Outliner = JArray.FromObject(outliners.Values);

            string content = JsonConvert.SerializeObject(blockBenchModel);
            File.WriteAllText(filepath, content);
        }

        private static Element CreateElement(SkinBOX box)
        {
            Vector3 transformPos = TranslateFromInternalPosistion(box, new Vector3(1, 1, 0));
            Element element = CreateElement(box.UV, transformPos, box.Size, box.Scale, box.Mirror);
            if (box.IsOverlayPart())
                element.Inflate = box.Type == "HEADWEAR" ? 0.5f : 0.25f;
            return element;
        }

        private static Element CreateElement(Vector2 uvOffset, Vector3 pos, Vector3 size, float inflate, bool mirror)
        {
            return Element.CreateCube("cube", uvOffset, pos, size, inflate, mirror);
        }

        private static Geometry GetGeometry(string filepath)
        {
            // Bedrock Entity (Model)
            if (filepath.EndsWith(".geo.json"))
            {
                BedrockModel bedrockModel = JsonConvert.DeserializeObject<BedrockModel>(File.ReadAllText(filepath));
                var availableModels = bedrockModel.Models.Select(m => m.Description.Identifier).ToArray();
                if (availableModels.Length < 2)
                    return availableModels.Length == 1 ? bedrockModel.Models[0] : null;

                using ItemSelectionPopUp itemSelectionPopUp = new ItemSelectionPopUp(availableModels);
                if (itemSelectionPopUp.ShowDialog() == DialogResult.OK && bedrockModel.Models.IndexInRange(itemSelectionPopUp.SelectedIndex))
                {
                    return bedrockModel.Models[itemSelectionPopUp.SelectedIndex];
                }
            }

            // Bedrock Legacy Model
            else if (filepath.EndsWith(".json"))
            {
                BedrockLegacyModel bedrockModel = JsonConvert.DeserializeObject<BedrockLegacyModel>(File.ReadAllText(filepath));
                var availableModels = bedrockModel.Select(m => m.Key).ToArray();
                if (availableModels.Length < 2)
                    return availableModels.Length == 1 ? bedrockModel[availableModels[0]] : null;
                using ItemSelectionPopUp itemSelectionPopUp = new ItemSelectionPopUp(availableModels);
                if (itemSelectionPopUp.ShowDialog() == DialogResult.OK && bedrockModel.ContainsKey(itemSelectionPopUp.SelectedItem))
                {
                    return bedrockModel[itemSelectionPopUp.SelectedItem];
                }
            }

            return null;
        }

        private static SkinModelInfo ImportBedrockJson(string filepath)
        {
            Geometry geometry = GetGeometry(filepath);
            if (geometry is null)
                return null;

            (IEnumerable<SkinBOX> boxes, IEnumerable<SkinPartOffset> partOffsets) = LoadGeometry(geometry);

            SkinModelInfo modelInfo = CreateSkinModelInfo(boxes, partOffsets);

            string texturePath = Path.Combine(Path.GetDirectoryName(filepath), Path.GetFileNameWithoutExtension(filepath)) + ".png";
            if (File.Exists(texturePath))
            {
                modelInfo.Texture = Image.FromFile(texturePath).ReleaseFromFile();
                modelInfo.Texture = SwapBoxBottomTexture(modelInfo);
            }

            if (geometry.Description?.TextureSize.Width == geometry.Description?.TextureSize.Height)
                modelInfo.ANIM = modelInfo.ANIM.SetFlag(SkinAnimFlag.RESOLUTION_64x64, true);

            return modelInfo;
        }

        private static (IEnumerable<SkinBOX> boxes, IEnumerable<SkinPartOffset> partOffsets) LoadGeometry(Geometry geometry)
        {
            List<SkinPartOffset> skinPartOffsets = new List<SkinPartOffset>();
            List<SkinBOX> boxes = new List<SkinBOX>();

            foreach (Bone bone in geometry.Bones)
            {
                string boxType = TryConvertToSkinBoxType(bone.Name);
                if (!SkinBOX.IsValidType(boxType))
                    continue;

                string offsetType = SkinBOX.IsOverlayPart(boxType) ? SkinBOXExtensions.GetBaseType(boxType) : boxType;
                Vector3 offset = GetOffsetFromOrigin(offsetType, bone.Pivot * new Vector3(-1, 1, 1));
                if (offset.Y != 0f)
                    skinPartOffsets.Add(new SkinPartOffset(offsetType, -offset.Y));

                foreach (External.Format.Cube cube in bone.Cubes)
                {
                    Vector3 pos = TranslateToInternalPosition(boxType, cube.Origin, cube.Size, Vector3.UnitY);
                    var skinBox = new SkinBOX(boxType, pos, cube.Size, cube.Uv, hideWithArmor: bone.Name == "helmet", mirror: cube.Mirror);
                    if (skinBox.IsBasePart() && ((boxType == "HEAD" && cube.Inflate == 0.5f) || (cube.Inflate >= 0.25f && cube.Inflate <= 0.5f)))
                        skinBox.Type = skinBox.GetOverlayType();
                    boxes.Add(skinBox);
                }
            }
            return (boxes, skinPartOffsets);
        }

        internal static void ExportBedrockJson(string filepath, SkinModelInfo modelInfo)
        {
            if (string.IsNullOrEmpty(filepath) || !filepath.EndsWith(".json"))
                return;

            Dictionary<string, Bone> bones = new Dictionary<string, Bone>(5);
            Dictionary<string, SkinPartOffset> offsetLookUp = new Dictionary<string, SkinPartOffset>(5);

            void AddBone(SkinBOX box)
            {
                string offsetType = box.IsOverlayPart() ? box.GetBaseType() : box.Type;

                Vector3 offset = GetOffsetForPart(offsetType, ref offsetLookUp, modelInfo.PartOffsets);

                if (!bones.ContainsKey(offsetType))
                {
                    Bone bone = new Bone(offsetType)
                    {
                        Pivot = GetSkinPartPivot(offsetType, new Vector3(0, 1, 0)) + offset
                    };
                    bones.Add(offsetType, bone);
                }
                Vector3 pivot = bones.ContainsKey(offsetType) ? bones[offsetType].Pivot : Vector3.Zero;
                Vector3 pos = TranslateFromInternalPosistion(box, new Vector3(1, 1, 0));
                pos = TransformSpace(pos, box.Size, new Vector3(1, 0, 0));

                bones[offsetType].Cubes.Add(new External.Format.Cube()
                {
                    Origin = pos + offset,
                    Size = box.Size,
                    Uv = box.UV,
                    Inflate = box.Scale + (box.IsOverlayPart() ? box.Type == "HEAD" ? 0.5f : 0.25f : 0f),
                    Mirror = box.Mirror,
                });
            }

            ANIM2BOX(modelInfo.ANIM, AddBone);

            foreach (SkinBOX box in modelInfo.AdditionalBoxes)
            {
                AddBone(box);
            }

            Geometry selectedGeometry = new Geometry();
            selectedGeometry.Bones.AddRange(bones.Values);
            object bedrockModel = null;
            // Bedrock Entity (Model)
            if (filepath.EndsWith(".geo.json"))
            {
                selectedGeometry.Description = new GeometryDescription()
                {
                    Identifier = $"geometry.{Application.ProductName}.{Path.GetFileNameWithoutExtension(filepath)}",
                    TextureSize = modelInfo.Texture.Size,
                };
                bedrockModel = new BedrockModel
                {
                    FormatVersion = "1.12.0",
                    Models = { selectedGeometry }
                };
            }
            // Bedrock Legacy Model
            else if (filepath.EndsWith(".json") && modelInfo.Texture.Height == modelInfo.Texture.Width)
            {
                bedrockModel = new BedrockLegacyModel
                {
                    { $"geometry.{Application.ProductName}.{Path.GetFileNameWithoutExtension(filepath)}", selectedGeometry }
                };
            }
            else
            {
                MessageBox.Show("Can't export to Bedrock Legacy Model.", "Invalid Texture Dimensions", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (bedrockModel is not null)
            {
                string content = JsonConvert.SerializeObject(bedrockModel);
                File.WriteAllText(filepath, content);
                string texturePath = Path.Combine(Path.GetDirectoryName(filepath), Path.GetFileNameWithoutExtension(filepath)) + ".png";
                SwapBoxBottomTexture(modelInfo).Save(texturePath, ImageFormat.Png);
            }
        }

        private static void ANIM2BOX(SkinANIM anim, Action<SkinBOX> converter)
        {
            bool isSlim = anim.GetFlag(SkinAnimFlag.SLIM_MODEL);
            bool is32x64 = !(anim.GetFlag(SkinAnimFlag.RESOLUTION_64x64) || isSlim);
            if (!anim.GetFlag(SkinAnimFlag.HEAD_DISABLED))
                converter(new SkinBOX("HEAD", new Vector3(-4, -8, -4), new Vector3(8), Vector2.Zero));

            if (!is32x64 && !anim.GetFlag(SkinAnimFlag.HEAD_OVERLAY_DISABLED))
                converter(new SkinBOX("HEADWEAR", new Vector3(-4, -8, -4), new Vector3(8), new Vector2(32, 0)));

            if (!anim.GetFlag(SkinAnimFlag.BODY_DISABLED))
                converter(new SkinBOX("BODY", new(-4, 0, -2), new(8, 12, 4), new(16, 16)));

            if (!is32x64 && !anim.GetFlag(SkinAnimFlag.BODY_OVERLAY_DISABLED))
                converter(new SkinBOX("JACKET", new(-4, 0, -2), new(8, 12, 4), new(16, 32)));

            if (!anim.GetFlag(SkinAnimFlag.RIGHT_ARM_DISABLED))
                converter(new SkinBOX("ARM0", new(isSlim ? -2 : - 3, -2, -2), new(isSlim ? 3 : 4, 12, 4), new(40, 16)));

            if (!is32x64 && !anim.GetFlag(SkinAnimFlag.RIGHT_ARM_OVERLAY_DISABLED))
                converter(new SkinBOX("SLEEVE0", new(isSlim ? -2 : - 3, -2, -2), new(isSlim ? 3 : 4, 12, 4), new(40, 32)));

            if (!anim.GetFlag(SkinAnimFlag.LEFT_ARM_DISABLED))
                converter(new SkinBOX("ARM1", new(-1, -2, -2), new(isSlim ? 3 : 4, 12, 4), is32x64 ? new(40, 16) : new(32, 48), mirror: is32x64));

            if (!is32x64 && !anim.GetFlag(SkinAnimFlag.LEFT_ARM_OVERLAY_DISABLED))
                converter(new SkinBOX("SLEEVE1", new(-1, -2, -2), new(isSlim ? 3 : 4, 12, 4), new(48, 48)));

            if (!anim.GetFlag(SkinAnimFlag.RIGHT_LEG_DISABLED))
                converter(new SkinBOX("LEG0", new(-2, 0, -2), new(4, 12, 4), new(0, 16)));

            if (!is32x64 && !anim.GetFlag(SkinAnimFlag.RIGHT_LEG_OVERLAY_DISABLED))
                converter(new SkinBOX("PANTS0", new(-2, 0, -2), new(4, 12, 4), new(0, 32)));

            if (!anim.GetFlag(SkinAnimFlag.LEFT_LEG_DISABLED))
            {
                converter(new SkinBOX("LEG1", new(-2, 0, -2), new(4, 12, 4), is32x64 ? new(0, 16) : new(16, 48), mirror: is32x64));
            }

            if (!is32x64 && !anim.GetFlag(SkinAnimFlag.LEFT_LEG_OVERLAY_DISABLED))
            {
                converter(new SkinBOX("PANTS1", new(-2, 0, -2), new(4, 12, 4), new(0, 48)));
            }
        }

        private static string TryConvertToSkinBoxType(string name)
        {
            if (!SkinBOX.IsValidType(name) && SkinBOX.IsValidType(name.ToUpper()))
            {
                return name.ToUpper();
            }
            return name.ToLower() switch
            {
                "helmet"      => "HEAD",
                "rightarm"    => "ARM0",
                "leftarm"     => "ARM1",
                "rightleg"    => "LEG0",
                "leftleg"     => "LEG1",
                "hat"         => "HEADWEAR",
                "bodyarmor"   => "BODY",
                "rightsleeve" => "SLEEVE0",
                "leftsleeve"  => "SLEEVE1",
                "rightpants"  => "PANTS0",
                "leftpants"   => "PANTS1",
                _             => name,
            };
        }

        private static Vector3 GetOffsetFromOrigin(string boxType, Vector3 origin)
        {
            Vector3 partTranslation = ModelPartSpecifics.GetPositioningInfo(boxType).Pivot;
            Vector3 offset = partTranslation - ((Vector3.UnitY * 24f) - origin);
            if (offset.X != 0f || offset.Z != 0f)
                Trace.TraceWarning($"[{nameof(SkinModelImporter)}:{nameof(GetOffsetFromOrigin)}] Warning: skin part({boxType}) offsets only support horizontal offsets.");
            return offset * Vector3.UnitY;
        }

        private static Vector3 GetSkinPartPivot(string partName, Vector3 translationUnit)
        {
            return TransformSpace(ModelPartSpecifics.GetPositioningInfo(partName).Pivot, Vector3.Zero, translationUnit) + (24f * Vector3.UnitY);
        }

        private static Vector3 GetOffsetForPart(string offsetType, ref Dictionary<string, SkinPartOffset> offsetLookUp, IEnumerable<SkinPartOffset> partOffsets)
        {
            if (offsetLookUp.ContainsKey(offsetType))
            {
                return -offsetLookUp[offsetType].Value * Vector3.UnitY;
            }
            if (partOffsets.Any(o => o.Type == offsetType))
            {
                SkinPartOffset partOffset = partOffsets.First(o => o.Type == offsetType);
                offsetLookUp.Add(offsetType, partOffset);
                return -partOffset.Value * Vector3.UnitY;
            }
            return Vector3.Zero;
        }

        private static Image SwapBoxBottomTexture(SkinModelInfo modelInfo)
        {
            return SwapTextureAreas(modelInfo.Texture, modelInfo.AdditionalBoxes.Where(box => !(box.Size == Vector3.One || box.Size == Vector3.Zero)).Select(box =>
            {
                var imgPos = Point.Truncate(new PointF(box.UV.X + box.Size.X + box.Size.Z, box.UV.Y));
                var area = new RectangleF(imgPos, Size.Truncate(new SizeF(box.Size.X, box.Size.Z)));
                return Rectangle.Truncate(area);
            }), RotateFlipType.RotateNoneFlipY);
        }

        private static Image SwapTextureAreas(Image texture, IEnumerable<Rectangle> areasToFix, RotateFlipType type)
        {
            if (texture == null)
            {
                Trace.TraceError($"[{nameof(SkinModelImporter)}:{nameof(SwapBoxBottomTexture)}] Failed to fix texture: texture is null.");
                return null;
            }
            areasToFix = areasToFix.Where(rect => rect.Size.Width > 0 && rect.Size.Height > 0);
            Image result = new Bitmap(texture);
            using var g = Graphics.FromImage(result);
            g.ApplyConfig(new GraphicsConfig()
            {
                InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor,
                PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality
            });
            foreach (Rectangle area in areasToFix)
            {
                Image targetAreaImage = texture.GetArea(area);
                targetAreaImage.RotateFlip(type);
                Region clip = g.Clip;
                g.SetClip(area);
                g.Clear(Color.Transparent);
                g.DrawImage(targetAreaImage, area.Location);
                g.Clip = clip;
            }
            return result;
        }

        private static Vector3 TranslateToInternalPosition(string boxType, Vector3 origin, Vector3 size, Vector3 translationUnit)
        {
            Vector3 pos = TransformSpace(origin, size, translationUnit);
            // Skin Renderer (and Game) specific offset value.
            pos.Y += 24f;

            // This will cancel out the part specific translation.
            Vector3 translation = ModelPartSpecifics.GetPositioningInfo(boxType).Translation;
            pos -= translation;

            return pos;
        }

        private static Vector3 TranslateFromInternalPosistion(SkinBOX skinBox, Vector3 translationUnit)
        {
            return TranslateToInternalPosition(skinBox.Type, skinBox.Pos, skinBox.Size, translationUnit);
        }
    }
}
