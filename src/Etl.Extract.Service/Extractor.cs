using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AngleSharp;
using Etl.Logger;
using Etl.Shared;
using Etl.Shared.CustomExceptions;
using Etl.Shared.Factories;
using Etl.Shared.FileLoader;
using Etl.Transform.Service;
using Microsoft.AspNetCore.Hosting;

namespace Etl.Extract.Service
{
    public class Extractor : IExtractor
    {

        private readonly ICustomLogger _logger;
        private readonly IHostingEnvironment _hostingEnvironment;
        private readonly ITransformer _transformerService;
        private ISender _sender;

        public Extractor(ICustomLogger logger, IHostingEnvironment hostingEnvironment, ITransformer transformerService)
        {
            _logger = logger;
            _hostingEnvironment = hostingEnvironment;
            _transformerService = transformerService;
        }

        public async Task Extract(WorkMode workMode, string basicUrl)
        {
            await InitSender(workMode);
            var numberOfPage = await GetNumberOfPages(basicUrl);
            _logger.Log($"Number of pages: {numberOfPage}");
            var articlesUrl = new List<string>();
            for (int i = 1; i <= numberOfPage; i++)
            {
                articlesUrl.AddRange(await GetArticlesUrlFromPage(basicUrl + "?page=" + i));
            }
            _logger.Log($"Number of articles: {articlesUrl.Count}");

            foreach (var url in articlesUrl)
            {
                await _sender.Send(await GetArticleContent(url));
            }
            await Task.CompletedTask;
        }

        private async Task<string> GetArticleContent(string url)
        {
            var config = Configuration.Default.WithDefaultLoader()
                .WithCss();
            try
            {
                var document = await BrowsingContext.New(config).OpenAsync(url);
                var errorMessage = document.All
                    .Where(x => x.Id == "ad-not-available-box").SingleOrDefault();
                if(errorMessage != null){
                    throw new ArticleNotAvailableException();
                }
                return document.All
                    .Where(x => x.Id == "siteWrap").Single().OuterHtml;
            }
            catch (ArticleNotAvailableException)
            {
                _logger.Log($"Article not available, url: {url}");
                return null;
            }
            catch
            {
                _logger.Log($"Error while getting content from: {url}");
                return null;
            }

        }

        private async Task<List<string>> GetArticlesUrlFromPage(string url)
        {
            var config = Configuration.Default.WithDefaultLoader()
                .WithCss();
            var document = await BrowsingContext.New(config).OpenAsync(url);
            var content = document.All
                .Where(x => x.LocalName == "article").Select(x => x.GetAttribute("data-href")).ToList();

            return await Task.FromResult(content);
        }

        private async Task<int> GetNumberOfPages(string url)
        {
            var config = Configuration.Default.WithDefaultLoader()
                .WithCss();
            var document = await BrowsingContext.New(config).OpenAsync(url);
            var bodySection = document.All
                .Where(x => x.Id == "body-container").Single();
            var paggerRow = bodySection
                .Children.Where(x => x.ClassName == "container-fluid container-fluid-sm")
                .Single()
                .Children.Where(x => x.ClassName == "row").ElementAt(1);

            if (paggerRow.FirstElementChild == null)
            {
                return await Task.FromResult<int>(1);
            }
            else
            {
                var numberOfPages = paggerRow.Children.Where(x => x.ClassName == "om-pager rel").Single()
                    .Children.Where(x => x.ClassName != "next abs").Last()
                    .Children.First()
                    .Children.First()
                    .InnerHtml;

                int integerNumber = 0;
                if (Int32.TryParse(numberOfPages, out integerNumber))
                {
                    return await Task.FromResult<int>(integerNumber);
                }
            }
            return await Task.FromResult<int>(0);
        }

        private async Task InitSender(WorkMode workMode)
        {
            var path = Path.Combine(_hostingEnvironment.ContentRootPath, "AfterExtract"); //todo path from config
            _sender = await new SenderFactory(workMode, path, _transformerService).GetSender();
        }
    }
}