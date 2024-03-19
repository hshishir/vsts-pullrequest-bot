using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace PullRequestBot.Common.Tests
{
    [TestClass]
    public class ExtensionsTests
    {
        [TestMethod]
        public void GivenValidLegacyUrl_WhenExtractAccountName_ThenReturnName()
        {
            var result = "https://devdiv-test.visualstudio.com".ExtractAccountName();
            Assert.AreEqual(result, "devdiv-test");
        }

        [TestMethod]
        public void GivenValidUrl_WhenExtractAccountName_ThenReturnName()
        {
            var result = "https://dev.azure.com/devdiv-test".ExtractAccountName();
            Assert.AreEqual(result, "devdiv-test");
        }

        [TestMethod]
        public void GivenInvalidUrl_WhenExtractAccountName_ThenReturnNull()
        {
            var result = "https://dev.snarky.com/devdiv-test".ExtractAccountName();
            Assert.IsNull(result);
        }

        [TestMethod]
        public void GivenNull_WhenGetParentGitDirectoryName_ThenReturnNull()
        {
            var result = (null as string).GetParentGitDirectoryName();
            Assert.IsNull(result);
        }

        [TestMethod]
        public void GivenWhitespace_WhenGetParentGitDirectoryName_ThenReturnNull()
        {
            var result = " ".GetParentGitDirectoryName();
            Assert.IsNull(result);
        }

        [TestMethod]
        public void GivenSlash_WhenGetParentGitDirectoryName_ThenReturnNull()
        {
            var result = "/".GetParentGitDirectoryName();
            Assert.IsNull(result);
        }

        [TestMethod]
        public void GivenValidPath_WhenGetParentGitDirectoryName_ThenReturnParent()
        {
            var result = "C:/repo/src/path".GetParentGitDirectoryName();
            Assert.AreEqual("C:/repo/src", result);
        }

        [TestMethod]
        public void GivenBackSlash_WhenGetParentGitDirectoryName_ThenReturnForwardSlash()
        {
            var result = @"C:\repo\src\path".GetParentGitDirectoryName();
            Assert.AreEqual("C:/repo/src", result);
        }

        [TestMethod]
        public void GivenDuplicateParentPaths_WhenGetUniqueParentGitDirectories_ThenExcludeDuplicates()
        {
            var paths = new[]
            {
                "repo/src/a/b",
                "repo/src/a/b/c",
                "repo/src/d/e/f"
            };
            var expect = new[]
            {
                "repo/src/a",
                "repo/src",
                "repo",
                "repo/src/a/b",
                "repo/src/d/e",
                "repo/src/d",
            };
            var result = paths.GetUniqueParentGitDirectories().ToList();
            CollectionAssert.AreEqual(expect, result);
        }
    }
}