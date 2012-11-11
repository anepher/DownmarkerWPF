﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Caliburn.Micro;
using MarkPad.DocumentSources.FileSystem;
using MarkPad.DocumentSources.GitHub;
using MarkPad.DocumentSources.MetaWeblog;
using MarkPad.DocumentSources.NewDocument;
using MarkPad.DocumentSources.WebSources;
using MarkPad.Infrastructure;
using MarkPad.Infrastructure.DialogService;
using MarkPad.Plugins;
using MarkPad.PreviewControl;
using MarkPad.Settings.Models;

namespace MarkPad.DocumentSources
{
    public class DocumentFactory : IDocumentFactory
    {
        readonly IDialogService dialogService;
        readonly IEventAggregator eventAggregator;
        readonly ISiteContextGenerator siteContextGenerator;
        readonly IBlogService blogService;
        readonly IWindowManager windowManager;
        readonly Lazy<IWebDocumentService> webDocumentService;
        readonly IFileSystem fileSystem;

        public DocumentFactory(
            IDialogService dialogService, 
            IEventAggregator eventAggregator,
            ISiteContextGenerator siteContextGenerator, 
            IBlogService blogService, 
            IWindowManager windowManager, 
            Lazy<IWebDocumentService> webDocumentService, 
            IFileSystem fileSystem)
        {
            this.dialogService = dialogService;
            this.eventAggregator = eventAggregator;
            this.siteContextGenerator = siteContextGenerator;
            this.blogService = blogService;
            this.windowManager = windowManager;
            this.webDocumentService = webDocumentService;
            this.fileSystem = fileSystem;
        }

        public IMarkpadDocument NewDocument()
        {
            return new NewMarkpadDocument(fileSystem, this, string.Empty);
        }

        public IMarkpadDocument NewDocument(string initalText)
        {
            return new NewMarkpadDocument(fileSystem, this, initalText);
        }

        public IMarkpadDocument CreateHelpDocument(string title, string content)
        {
            return new HelpDocument(title, content, this, fileSystem);
        }
        
        public async Task<IMarkpadDocument> OpenDocument(string path)
        {
            var contents = await fileSystem.File.ReadAllTextAsync(path);
            var siteContext = siteContextGenerator.GetContext(path);

            var associatedImages = GetAssociatedImages(contents, siteContext);

            return new FileMarkdownDocument(path, contents, siteContext, associatedImages, this, eventAggregator, dialogService, fileSystem);
        }

        IEnumerable<FileReference> GetAssociatedImages(string markdownFileContents, ISiteContext siteContext)
        {
            const string imageRegex = @"!\[(?<AltText>.*?)\]\((?<Link>.*?)\)";
            var images = Regex.Matches(markdownFileContents, imageRegex);
            var associatedImages = new List<FileReference>();

            foreach (Match image in images)
            {
                var imageLink = image.Groups["Link"].Value;
                if (Path.IsPathRooted(imageLink) && fileSystem.File.Exists(imageLink))
                    associatedImages.Add(new FileReference(image.Value, image.Value, true));
                else
                {
                    var fullPath = Path.Combine(siteContext.WorkingDirectory, imageLink);
                    if (fileSystem.File.Exists(fullPath))
                        associatedImages.Add(new FileReference(fullPath, imageLink, true));
                }
            }
            return associatedImages;
        }

        /// <summary>
        /// Publishes any document
        /// </summary>
        /// <param name="postId"></param>
        /// <param name="document"></param>
        /// <returns></returns>
        public Task<IMarkpadDocument> PublishDocument(string postId, IMarkpadDocument document)
        {
            var blogs = blogService.GetBlogs();
            if (blogs == null || blogs.Count == 0)
            {
                if (!blogService.ConfigureNewBlog("Publish document"))
                    return TaskEx.FromResult<IMarkpadDocument>(null);
                blogs = blogService.GetBlogs();
                if (blogs == null || blogs.Count == 0)
                    return TaskEx.FromResult<IMarkpadDocument>(null);
            }

            var categories = new List<string>();
            var webDocument = document as WebDocument;
            if (webDocument != null)
                categories = webDocument.Categories;
            var pd = new Details { Title = document.Title, Categories = categories.ToArray()};
            var detailsResult = windowManager.ShowDialog(new PublishDetailsViewModel(pd, blogs));
            if (detailsResult != true)
                return TaskEx.FromResult<IMarkpadDocument>(null);

            var newDocument = new WebDocument(pd.Blog, null, pd.Title, document.MarkdownContent, new FileReference[0], this,
                                              webDocumentService.Value, siteContextGenerator.GetWebContext(pd.Blog), fileSystem);

            foreach (var associatedFile in document.AssociatedFiles)
            {
                newDocument.AddFile(new FileReference(associatedFile.FullPath, associatedFile.RelativePath, false));
            }

            return newDocument.Save();
        }

        public async Task<IMarkpadDocument> OpenBlogPost(BlogSetting blog, string id, string name)
        {
            var metaWeblogSiteContext = siteContextGenerator.GetWebContext(blog);

            var content = await webDocumentService.Value.GetDocumentContent(blog, id);

            return new WebDocument(blog, id, name, content, new FileReference[0], this, webDocumentService.Value, metaWeblogSiteContext, fileSystem);
        }

        public async Task<IMarkpadDocument> SaveDocumentAs(IMarkpadDocument document)
        {
            var path = dialogService.GetFileSavePath("Save As", "*.md", Constants.ExtensionFilter + "|All Files (*.*)|*.*");

            if (string.IsNullOrEmpty(path))
                throw new TaskCanceledException("Save As Cancelled");

            await fileSystem.File.WriteAllTextAsync(path, document.MarkdownContent);

            var siteContext = siteContextGenerator.GetContext(path);
            var newMarkdownFile = new FileMarkdownDocument(path, document.MarkdownContent, siteContext, new FileReference[0],  this, eventAggregator, dialogService, fileSystem);

            SaveAndRewriteImages(document, newMarkdownFile);

            return newMarkdownFile;
        }

        void SaveAndRewriteImages(IMarkpadDocument document, IMarkpadDocument newMarkdownFile)
        {
            foreach (var associatedFile in document.AssociatedFiles)
            {
                var fileReference = newMarkdownFile.SaveImage(fileSystem.OpenBitmap(associatedFile.FullPath));
                newMarkdownFile.MarkdownContent = newMarkdownFile.MarkdownContent.Replace(associatedFile.RelativePath, fileReference.RelativePath);
            }
        }
    }
}