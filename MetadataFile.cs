using System;
using System.Collections.Generic;
using System.IO;

namespace MetaDataStringEditor {
    class MetadataFile : IDisposable {
        private const uint MetadataSanity = 0xFAB11BAF;
        private const int OldStringLiteralSize = 8;
        private const int NewStringLiteralSize = 4;
        private const int SectionMetadataSize = 12;

        public BinaryReader reader;

        private int version;
        private MetadataLayout layout;
        private SectionInfo stringLiteralSection;
        private SectionInfo stringLiteralDataSection;
        private List<StringLiteral> stringLiterals = new List<StringLiteral>();
        public List<byte[]> strBytes = new List<byte[]>();

        public MetadataFile(string fullName) {
            reader = new BinaryReader(File.OpenRead(fullName));

            // 读取文件
            ReadHeader();

            // 读取字符串
            ReadLiteral();
            ReadStrByte();

            Logger.I("基础读取完成");
        }

        private void ReadHeader() {
            Logger.I("读取头部");
            uint sanity = reader.ReadUInt32();
            if (sanity != MetadataSanity) {
                throw new Exception("标志检查不通过");
            }

            version = reader.ReadInt32();
            if (version >= 38) {
                layout = MetadataLayout.NewSections;
                stringLiteralSection = ReadSection(8);
                stringLiteralDataSection = ReadSection(8 + SectionMetadataSize);
            } else {
                layout = version >= 35 ? MetadataLayout.DataIndexOnlyLiterals : MetadataLayout.LengthAndOffsetLiterals;
                reader.BaseStream.Position = 8;
                stringLiteralSection = new SectionInfo {
                    Offset = reader.ReadUInt32(),
                    Size = reader.ReadUInt32(),
                    OffsetFieldPosition = 8,
                    SizeFieldPosition = 12,
                    CountFieldPosition = -1
                };
                stringLiteralDataSection = new SectionInfo {
                    Offset = reader.ReadUInt32(),
                    Size = reader.ReadUInt32(),
                    OffsetFieldPosition = 16,
                    SizeFieldPosition = 20,
                    CountFieldPosition = -1
                };
            }

            ValidateSection("StringLiteral", stringLiteralSection);
            ValidateSection("StringLiteralData", stringLiteralDataSection);
            Logger.I($"metadata v{version}");
        }

        private void ReadLiteral() {
            Logger.I("读取Literal");
            int entrySize = GetStringLiteralEntrySize();
            int count = GetStringLiteralEntryCount(entrySize);
            ProgressBar.SetMax(count);

            reader.BaseStream.Position = stringLiteralSection.Offset;
            for (int i = 0; i < count; i++) {
                if (layout == MetadataLayout.LengthAndOffsetLiterals) {
                    stringLiterals.Add(new StringLiteral {
                        Length = reader.ReadUInt32(),
                        Offset = reader.ReadUInt32()
                    });
                } else {
                    stringLiterals.Add(new StringLiteral {
                        Offset = reader.ReadUInt32()
                    });
                }
                ProgressBar.Report();
            }

            if (layout != MetadataLayout.LengthAndOffsetLiterals) {
                if (stringLiterals.Count < 2) {
                    throw new Exception("StringLiteral 表数据不足，无法按新版格式解析");
                }

                for (int i = 0; i < stringLiterals.Count - 1; i++) {
                    uint current = stringLiterals[i].Offset;
                    uint next = stringLiterals[i + 1].Offset;
                    if (next < current) {
                        throw new Exception("StringLiteral DataIndex 非递增，metadata 可能不受支持");
                    }
                    stringLiterals[i].Length = next - current;
                }
            }
        }

        private void ReadStrByte() {
            Logger.I("读取字符串的Bytes");
            int editableCount = GetEditableLiteralCount();
            ProgressBar.SetMax(editableCount);

            for (int i = 0; i < editableCount; i++) {
                reader.BaseStream.Position = stringLiteralDataSection.Offset + stringLiterals[i].Offset;
                strBytes.Add(reader.ReadBytes((int)stringLiterals[i].Length));
                ProgressBar.Report();
            }
        }

        public void WriteToNewFile(string fileName) {
            using (BinaryWriter writer = new BinaryWriter(File.Create(fileName))) {
                // 先全部复制过去
                reader.BaseStream.Position = 0;
                reader.BaseStream.CopyTo(writer.BaseStream);

                // 更新Literal
                Logger.I("更新Literal");
                ProgressBar.SetMax(strBytes.Count);
                writer.BaseStream.Position = stringLiteralSection.Offset;
                uint count = 0;
                for (int i = 0; i < strBytes.Count; i++) {
                    stringLiterals[i].Offset = count;
                    stringLiterals[i].Length = (uint)strBytes[i].Length;

                    if (layout == MetadataLayout.LengthAndOffsetLiterals) {
                        writer.Write(stringLiterals[i].Length);
                        writer.Write(stringLiterals[i].Offset);
                    } else {
                        writer.Write(stringLiterals[i].Offset);
                    }

                    count += stringLiterals[i].Length;
                    ProgressBar.Report();
                }

                if (layout != MetadataLayout.LengthAndOffsetLiterals) {
                    writer.Write(count);
                }

                // 检查是否够空间放置
                if (count > stringLiteralDataSection.Size) {
                    // 检查数据区后面还有没有别的数据，没有就可以直接延长数据区
                    if (stringLiteralDataSection.Offset + stringLiteralDataSection.Size < writer.BaseStream.Length) {
                        // 原有空间不够放，也不能直接延长，所以整体挪到文件尾
                        stringLiteralDataSection.Offset = Align4((uint)writer.BaseStream.Length);
                    }
                }

                // 进行一次对齐，不确定是否一定需要，但是Unity是做了，所以还是补上为好
                count = Align4(stringLiteralDataSection.Offset + count) - stringLiteralDataSection.Offset;
                stringLiteralDataSection.Size = count;
                if (layout == MetadataLayout.NewSections) {
                    stringLiteralDataSection.Count = count;
                }

                // 写入string
                Logger.I("更新String");
                ProgressBar.SetMax(strBytes.Count);
                writer.BaseStream.Position = stringLiteralDataSection.Offset;
                for (int i = 0; i < strBytes.Count; i++) {
                    writer.Write(strBytes[i]);
                    ProgressBar.Report();
                }

                // 更新头部
                Logger.I("更新头部");
                WriteSection(writer, stringLiteralDataSection);

                Logger.I("更新完成");
            }
        }

        private SectionInfo ReadSection(long position) {
            reader.BaseStream.Position = position;
            return new SectionInfo {
                Offset = reader.ReadUInt32(),
                Size = reader.ReadUInt32(),
                Count = reader.ReadUInt32(),
                OffsetFieldPosition = position,
                SizeFieldPosition = position + 4,
                CountFieldPosition = position + 8
            };
        }

        private void WriteSection(BinaryWriter writer, SectionInfo section) {
            writer.BaseStream.Position = section.OffsetFieldPosition;
            writer.Write(section.Offset);
            writer.Write(section.Size);
            if (section.CountFieldPosition >= 0) {
                writer.Write(section.Count);
            }
        }

        private void ValidateSection(string name, SectionInfo section) {
            if (section.Offset > reader.BaseStream.Length || section.Offset + section.Size > reader.BaseStream.Length) {
                throw new Exception($"{name} 区域越界，metadata 可能已加密或版本不受支持");
            }
        }

        private int GetStringLiteralEntrySize() {
            return layout == MetadataLayout.LengthAndOffsetLiterals ? OldStringLiteralSize : NewStringLiteralSize;
        }

        private int GetStringLiteralEntryCount(int entrySize) {
            if (layout == MetadataLayout.NewSections) {
                if (stringLiteralSection.Count <= 0) {
                    throw new Exception("StringLiteral 表数量为空");
                }
                return checked((int)stringLiteralSection.Count);
            }

            if (stringLiteralSection.Size % entrySize != 0) {
                throw new Exception("StringLiteral 表长度与当前版本结构大小不匹配");
            }
            return checked((int)(stringLiteralSection.Size / entrySize));
        }

        private int GetEditableLiteralCount() {
            return layout == MetadataLayout.LengthAndOffsetLiterals ? stringLiterals.Count : stringLiterals.Count - 1;
        }

        private uint Align4(uint value) {
            uint remainder = value % 4;
            return remainder == 0 ? value : value + 4 - remainder;
        }

        public void Dispose() {
            reader?.Dispose();
        }

        public class StringLiteral {
            public uint Length;
            public uint Offset;
        }

        private enum MetadataLayout {
            LengthAndOffsetLiterals,
            DataIndexOnlyLiterals,
            NewSections
        }

        private class SectionInfo {
            public uint Offset;
            public uint Size;
            public uint Count;
            public long OffsetFieldPosition;
            public long SizeFieldPosition;
            public long CountFieldPosition;
        }
    }
}
