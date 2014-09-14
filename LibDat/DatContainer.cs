using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using LibDat.Data;
using System.Text.RegularExpressions;

namespace LibDat
{
    /// <summary>
    /// Parses and holds all information found in a specific .dat file.
    /// </summary>
    public class DatContainer
    {
        /// <summary>
        /// Name of the dat file (without .dat extension)
        /// </summary>
        public readonly string DatName;

        /// <summary>
        /// Length of .dat file
        /// </summary>
        public int Length { get; private set; }

        public RecordInfo RecordInfo { get; private set; }

        /// <summary>
        /// Offset of the data section in the .dat file (Starts with 0xbbbbbbbbbbbbbbbb)
        /// </summary>
        public static int DataSectionOffset { get; private set; }

        /// <summary>
        /// Length of data section of .dat file
        /// </summary>
        public int DataSectionDataLength { get; private set; }

        /// <summary>
        /// Contains the entire .dat file
        /// </summary>
        private byte[] _originalData;

        /// <summary>
        /// List of .dat files records' content
        /// </summary>
        public List<RecordData> Records;

        /// <summary>
        /// Mapping of all known strings and other data found in the data section. 
        /// Key = offset with respect to beginning of data section.
        /// 
        /// </summary>
        public static Dictionary<int, AbstractData> DataEntries { get; private set; }

        public static Dictionary<int, PointerData> DataPointers { get; private set; }

        /// <summary>
        /// Parses the .dat file contents from inStream.
        /// </summary>
        /// <param name="inStream">Unicode binary reader containing ONLY the contents of a single .dat file and nothing more</param>
        /// <param name="fileName">Name of the dat file (with extension)</param>
        public DatContainer(Stream inStream, string fileName)
        {
            DatName = Path.GetFileNameWithoutExtension(fileName);
            RecordInfo = RecordFactory.GetRecordInfo(DatName);
            DataEntries = new Dictionary<int, AbstractData>();
            DataPointers = new Dictionary<int, PointerData>();

            using (var br = new BinaryReader(inStream, Encoding.Unicode))
            {
                Read(br);
            }
        }

        /// <summary>
        /// Parses the .dat file found at path 'fileName'
        /// </summary>
        /// <param name="filePath">Path of .dat file to parse</param>
        public DatContainer(string filePath)
        {
            DatName = Path.GetFileNameWithoutExtension(filePath);
            RecordInfo = RecordFactory.GetRecordInfo(DatName);
            DataEntries = new Dictionary<int, AbstractData>();
            DataPointers = new Dictionary<int, PointerData>();

            var fileBytes = File.ReadAllBytes(filePath);

            using (var ms = new MemoryStream(fileBytes))
            {
                using (var br = new BinaryReader(ms, Encoding.Unicode))
                {
                    Read(br);
                }
            }
        }

        /// <summary>
        /// Reads the .dat frile from the specified stream
        /// </summary>
        /// <param name="inStream">Stream containing contents of .dat file</param>
        private void Read(BinaryReader inStream)
        {
            Length = (int)inStream.BaseStream.Length;
            DataSectionOffset = 0;

            // check that record format is defined
            if (RecordInfo == null)
                throw new Exception("Missing dat parser for file " + DatName);

            var numberOfEntries = inStream.ReadInt32();

            // find record_length;
            var recordLength = FindRecordLength(inStream, numberOfEntries);
            if ((numberOfEntries > 0) && recordLength != RecordInfo.Length)
                throw new Exception("Found record length = " + recordLength
                    + " not equal length defined in XML: " + RecordInfo.Length);
            if (RecordInfo.Length == 0)
                numberOfEntries = 0;

            // Data section offset
            DataSectionOffset = numberOfEntries * recordLength + 4;
            DataSectionDataLength = Length - DataSectionOffset - 8;
            inStream.BaseStream.Seek(DataSectionOffset, SeekOrigin.Begin);
            // check magic number
            if (inStream.ReadUInt64() != 0xBBbbBBbbBBbbBBbb)
                throw new ApplicationException("Missing magic number after records");

            // save entire stream
            inStream.BaseStream.Seek(0, SeekOrigin.Begin);
            _originalData = inStream.ReadBytes(Length);

            // read records
            Records = new List<RecordData>(numberOfEntries);
            for (var i = 0; i < numberOfEntries; i++)
            {
                Records.Add(new RecordData(RecordInfo, inStream, i));
            }
        }

        private static int FindRecordLength(BinaryReader inStream, int numberOfEntries)
        {
            if (numberOfEntries == 0)
                return 0;

            inStream.BaseStream.Seek(4, SeekOrigin.Begin);
            var stringLength = inStream.BaseStream.Length;
            var recordLength = 0;
            for (var i = 0; inStream.BaseStream.Position <= stringLength - 8; i++)
            {
                var ul = inStream.ReadUInt64();
                if (ul == 0xBBbbBBbbBBbbBBbb)
                {
                    recordLength = i;
                    break;
                }
                inStream.BaseStream.Seek(-8 + numberOfEntries, SeekOrigin.Current);
            }
            inStream.BaseStream.Seek(4, SeekOrigin.Begin);
            return recordLength;
        }

        /// <summary>
        /// Saves parsed data to specified path
        /// </summary>
        /// <param name="filePath">Path to write contents to</param>
        public void Save(string filePath)
        {
            using (var outStream = File.Open(filePath, FileMode.Create))
            {
                Save(outStream);
            }
        }

        public byte[] SaveAsBytes()
        {
            using (var ms = new MemoryStream())
            {
                Save(ms);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Saves possibly changed data to specified stream
        /// </summary>
        /// <param name="rawOutStream">Stream to write contents to</param>
        public void Save(Stream rawOutStream)
        {
            // Mapping of the new string and data offsets
            var changedStringOffsets = new Dictionary<int, int>();

            var outStream = new BinaryWriter(rawOutStream, Encoding.Unicode);

            // write original data
            outStream.Write(_originalData);

            // write changed strings to end of stream
            foreach (var item in DataEntries)
            {
                if (!(item.Value is StringData)) continue;

                var str = item.Value as StringData;
                if (string.IsNullOrWhiteSpace(str.NewValue)) continue;

                // actually write changed string
                var newOffset = (int) outStream.BaseStream.Position - DataSectionOffset;
                str.Save(outStream);
                changedStringOffsets[str.Offset] = newOffset;
            }

            // update pointer to string with changed offset
            foreach (var keyValuePair in DataPointers)
            {
                var offset = keyValuePair.Key;
                var pData = keyValuePair.Value;
                if (!(pData.RefData is StringData))
                    continue;
                var pOffset = pData.RefData.Offset;
                if (!changedStringOffsets.ContainsKey(pOffset)) 
                    continue;

                // StringData will write pointer to itself to pointer data
                outStream.Seek(DataSectionOffset + pData.Offset, SeekOrigin.Begin);
                pData.RefData.WritePointer(outStream);
            }
        }

        public IList<StringData> GetUserStrings()
        {
            var offsets = GetUserStringOffsets();
            return offsets.Select(offset => DataEntries[offset]).Cast<StringData>().ToList();
        }

        public IList<int> GetUserStringOffsets()
        {
            var result = new List<int>();

            // Get field which can contain user strings
            var indexes = RecordInfo.Fields.Where(f => f.IsPointer && f.IsUser).Select(f => f.Index).ToList();

            // Replace the actual strings
            foreach (var recordData in Records)
            {
                foreach (var index in indexes)
                {
                    var fieldData = recordData.FieldsData[index];
                    // TODO: find all refrenced string recursively
                    FindUserStrings(fieldData.Data, result);
                }
            }
            return result;
        }

        private void FindUserStrings(AbstractData data, List<int> result)
        {
            if (data == null)
                return;

            if (data is PointerData)
            {
                var pData = data as PointerData;
                FindUserStrings(pData.RefData, result);
            }
            else if (data is ListData)
            {
                var lData = data as ListData;
                foreach (var listEntry in lData.List)
                {
                    FindUserStrings(listEntry, result);
                }
            }
            else if (data is StringData)
            {
                var sData = data as StringData;
                if (!String.IsNullOrEmpty(sData.Value))
                    result.Add(data.Offset);
            }
            // skip any other value data
        }

        /// <summary>
        /// Returns a CSV table with the contents of this dat container.
        /// </summary>
        /// <returns></returns>
        public string GetCSV()
        {
            const char separator = ',';
            var sb = new StringBuilder();
            var fieldInfos = RecordInfo.Fields;

            // add header
            sb.AppendFormat("Rows{0}", separator);
            foreach (var field in fieldInfos)
            {
                sb.AppendFormat("{0}{1}", field.Id, separator);
            }
            sb.Remove(sb.Length - 1, 1);
            sb.AppendLine();

            // add records
            foreach (var recordData in Records)
            {
                // row index
                sb.AppendFormat("{0}{1}", Records.IndexOf(recordData), separator);

                // add fields
                foreach (var fieldData in recordData.FieldsData)
                {
                    sb.AppendFormat("{0}{1}", GetCSVString(fieldData), separator);
                }

                // finish line
                sb.Remove(sb.Length - 1, 1);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public string GetCSVString(FieldData fieldData)
        {
            string str;
            if (fieldData.FieldInfo.IsPointer)
            {
                var offset = fieldData.Data.Offset;
                if (DataEntries.ContainsKey(offset))
                {
                    str = DataEntries[offset].GetValueString();
                }
                else
                {
                    str = "Unknown data at offset " + offset;
                }
                if (String.IsNullOrEmpty(str))
                {
                    str = "[Empty Data]@" + offset;
                }
            }
            else // not a pointer type field
            {
                str = fieldData.Data.ToString();
            }

            str = (Regex.IsMatch(str, ",") ? String.Format("\"{0}\"", str.Replace("\"", "\"\"")) : str);
            return str;
        }
    }
}
