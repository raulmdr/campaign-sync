﻿using CsvHelper;
using log4net;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Zone.Campaign.Templates.Services;
using Zone.Campaign.WebServices.Model;
using Zone.Campaign.WebServices.Security;
using Zone.Campaign.WebServices.Services;

namespace Zone.Campaign.Sync.Services
{
    public class Uploader : IUploader
    {
        #region Fields

        private static readonly ILog Log = LogManager.GetLogger(typeof(Uploader));

        private readonly IMappingFactory _mappingFactory;

        private readonly IMetadataExtractorFactory _metadataExtractorFactory;

        private readonly ITemplateTransformerFactory _templateTransformerFactory;

        private readonly IBuilderService _builderService;

        private readonly IImageWriteService _imageWriteService;

        private readonly IWriteService _writeService;

        #endregion

        #region Constructor

        public Uploader(IMappingFactory mappingFactory,
                        IMetadataExtractorFactory metadataExtractorFactory,
                        ITemplateTransformerFactory templateTransformerFactory,
                        IBuilderService builderService,
                        IImageWriteService imageWriteService,
                        IWriteService writeService)
        {
            _mappingFactory = mappingFactory;
            _metadataExtractorFactory = metadataExtractorFactory;
            _templateTransformerFactory = templateTransformerFactory;
            _builderService = builderService;
            _imageWriteService = imageWriteService;
            _writeService = writeService;
        }

        #endregion

        #region Methods

        public void DoUpload(Uri rootUri, Tokens tokens, UploadSettings settings)
        {
            var pathList = settings.FilePaths.SelectMany(i =>
            {
                if (File.Exists(i))
                {
                    return new[] { i };
                }

                if (Directory.Exists(i))
                {
                    return Directory.GetFiles(i, "*", SearchOption.AllDirectories);
                }

                var dir = Path.GetDirectoryName(i);
                if (Directory.Exists(dir))
                {
                    return Directory.GetFiles(dir, Path.GetFileName(i), SearchOption.AllDirectories);
                }

                Log.WarnFormat("{0} specified for upload but no matching files found.", i);
                return new string[0];
            }).ToArray();

            var templateList = pathList.Select(i =>
            {
                var fileExtension = Path.GetExtension(i);
                var metadataExtractor = _metadataExtractorFactory.GetExtractor(fileExtension);
                if (metadataExtractor == null)
                {
                    Log.WarnFormat("Unsupported filetype {0}.", i);
                    return null;
                }

                var raw = File.ReadAllText(i);
                var template = metadataExtractor.ExtractMetadata(raw);

                if (template.Metadata == null)
                {
                    Log.WarnFormat("No metadata found in {0}.", i);
                    return template;
                }
                else if (template.Metadata.Schema == null)
                {
                    Log.WarnFormat("No schema found in {0}.", i);
                    return template;
                }
                else if (template.Metadata.Name == null)
                {
                    Log.WarnFormat("No name found in {0}.", i);
                    return template;
                }

                // TODO: I think maybe this should be set by the mapping, not the file extension.
                var templateTransformer = _templateTransformerFactory.GetTransformer(fileExtension);
                var workingDirectory = Path.GetDirectoryName(i);
                template.Code = templateTransformer.Transform(template.Code, workingDirectory);

                return template;
            }).Where(i => i != null && i.Metadata != null && i.Metadata.Schema != null && i.Metadata.Name != null)
              .ToArray();

            if (settings.TestMode)
            {
                Log.InfoFormat("{1} files found:{0}{2}", Environment.NewLine, templateList.Count(), string.Join(Environment.NewLine, templateList.Select(i => i.Metadata.Name)));
            }
            else
            {
                var templateCount = 0;
                foreach (var template in templateList)
                {
                    // Get mapping for defined schema, and generate object for write
                    var mapping = _mappingFactory.GetMapping(template.Metadata.Schema.ToString());
                    var persistable = mapping.GetPersistableItem(template);

                    var response = _writeService.Write(rootUri, tokens, persistable);
                    if (!response.Success)
                    {
                        Log.WarnFormat("Upload of {0} failed: {1}", template.Metadata.Name, response.Message);
                    }
                    else
                    {
                        templateCount++;
                    }
                }

                Log.InfoFormat("{0} files uploaded.", templateCount);

                var schemaList = templateList.Where(i => i.Metadata.Schema.ToString() == SrcSchema.Schema).ToArray();
                var schemaCount = 0;
                foreach (var schema in schemaList)
                {
                    var response = _builderService.BuildSchema(rootUri, tokens, schema.Metadata.Name);
                    if (!response.Success)
                    {
                        Log.WarnFormat("Build of {0} failed: {1}", schema.Metadata.Name, response.Message);
                    }
                    else
                    {
                        schemaCount++;
                    }
                }

                Log.InfoFormat("{0} schemas built.", schemaCount);
            }
        }

        public void DoImageUpload(Uri rootUri, Tokens tokens, UploadSettings settings)
        {
            var imageData = settings.FilePaths.SelectMany(i => GetImageData(i)).ToArray();

            var imageCount = 0;
            foreach (var imageItem in imageData)
            {
                var fileInfo = new FileInfo(imageItem.FilePath);

                string md5Hash;
                using (var md5 = MD5.Create())
                {
                    using (var stream = File.OpenRead(imageItem.FilePath))
                    {
                        md5Hash = BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLower();
                    }
                }

                int width, height;
                using (var bitmap = new Bitmap(imageItem.FilePath))
                {
                    width = bitmap.Width;
                    height = bitmap.Height;
                }

                var fileContent = Convert.ToBase64String(File.ReadAllBytes(imageItem.FilePath));

                var file = new ImageFile
                {
                    FolderName = imageItem.FolderName,
                    FileRes = new FileRes
                    {
                        Name = InternalName.Parse(imageItem.InternalName),
                        Label = imageItem.Label,
                        Alt = imageItem.Alt,
                        Width = width,
                        Height = height,
                    },
                    FileName = fileInfo.Name,
                    MimeType = GetMimeTypeFromExtension(Path.GetExtension(imageItem.FilePath)),
                    Md5 = md5Hash,
                    FileContent = fileContent,
                };

                var response = _imageWriteService.WriteImage(rootUri, tokens, file);
                if (!response.Success)
                {
                    Log.WarnFormat("Upload of {0} failed: {1}", imageItem.InternalName, response.Message);
                }
                else
                {
                    imageCount++;
                }
            }

            Log.InfoFormat("{0} images uploaded.", imageCount);
        }

        #endregion

        #region Helpers

        private string GetMimeTypeFromExtension (string extension)
        {
            switch (extension)
            {
                case ".gif":
                    return "image/gif";
                case ".jpg":
                    return "image/jpg";
                case ".png":
                    return "image/png";
                default:
                    return null;
            }
        }

        private IEnumerable<ImageData> GetImageData(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Log.WarnFormat("File {0} does not exist.", filePath);
                return Enumerable.Empty<ImageData>();
            }

            IEnumerable<ImageData> data;
            using (var textReader = new StreamReader(filePath))
            using (var csv = new CsvReader(textReader))
            {
                try
                {
                    data = csv.GetRecords<ImageData>().ToArray();
                }
                catch (CsvMissingFieldException ex)
                {
                    Log.ErrorFormat("File {0} does not appear to contain the correct data: {1}", filePath, ex.Message);
                    return Enumerable.Empty<ImageData>();
                }
            }

            var root = Path.GetDirectoryName(filePath);
            foreach (var item in data)
            {
                if (!Path.IsPathRooted(item.FilePath))
                {
                    item.FilePath = Path.Combine(root, item.FilePath);
                }
            }

            return data;
        }

        #endregion

        #region Classes

        private class ImageData
        {
            public string FilePath { get; set; }

            public string FolderName { get; set; }

            public string InternalName { get; set; }

            public string Label { get; set; }

            public string Alt { get; set; }
        }

        #endregion
    }
}