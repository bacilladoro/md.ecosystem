﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Schema;
using LibGit2Sharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PowerArgs;

namespace vpm
{
    public enum MachineType
    {
        Native = 0, x86 = 0x014c, Itanium = 0x0200, x64 = 0x8664
    }

    public static class VpmUtils
    {
        public static MachineType GetMachineType(string fileName)
        {
            const int PE_POINTER_OFFSET = 60;
            const int MACHINE_OFFSET = 4;
            byte[] data = new byte[4096];
            using (Stream s = new FileStream(fileName, FileMode.Open, FileAccess.Read))
            {
                s.Read(data, 0, 4096);
            }
            // dos header is 64 bytes, last element, long (4 bytes) is the address of the PE header
            int PE_HEADER_ADDR = BitConverter.ToInt32(data, PE_POINTER_OFFSET);
            int machineUint = BitConverter.ToUInt16(data, PE_HEADER_ADDR + MACHINE_OFFSET);
            return (MachineType) machineUint;
        }

        public static void CleanUp()
        {
            Directory.Delete(VpmConfig.Instance.VpmTempDir, true);
        }

        public static bool PromptYayOrNay(string question, string note = "")
        {
            bool first = true;
            while (true)
            {
                if (first)
                {
                    Console.WriteLine(question + " (Yay or Nay)");
                    if (!string.IsNullOrEmpty(note)) Console.WriteLine(note);
                }
                var decision = Console.ReadLine();
                if (decision == null) continue;
                if (decision.Contains("y") || decision.Contains("Y"))
                {
                    return true;
                }
                if (decision.Contains("n") || decision.Contains("N"))
                {
                    return false;
                }
                first = false;
            }
        }

        public static void ConfirmContinue()
        {
            if (Args.GetAmbientArgs<VpmArgs>().Quiet) return;
            if (PromptYayOrNay("Do you still want to continue?")) return;
            CleanUp();
            Environment.Exit(0);
        }

        public static bool IsAliasExisting(string name)
        {
            var packsdir = Path.GetDirectoryName(Args.GetAmbientArgs<VpmArgs>().VVVVExe) + "\\packs";
            foreach (var d in Directory.GetDirectories(packsdir))
            {
                var cname = Path.GetFullPath(d)
                    .TrimEnd(Path.DirectorySeparatorChar)
                    .Split(Path.DirectorySeparatorChar)
                    .Last();
                if (string.Equals(cname, name, StringComparison.InvariantCultureIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsPackExisting(string name, IEnumerable<string> aliases, out string matched)
        {
            if (IsAliasExisting(name))
            {
                matched = name;
                return true;
            }
            foreach (var a in aliases.Where(IsAliasExisting))
            {
                matched = a;
                return true;
            }
            matched = "";
            return false;
        }

        public static XmlDocument ParseAndValidateXmlFile(string xmlfile)
        {

            var xmlsettings = new XmlReaderSettings
            {
                Async = false,
                ValidationType = ValidationType.DTD
            };
            xmlsettings.ValidationEventHandler += (sender, args) =>
            {
                throw new Exception(".vpack validation error: " + args.Message);
            };
            var reader = XmlReader.Create(Args.GetAmbientArgs<VpmArgs>().VPackFile, xmlsettings);
            while (reader.Read()) ;

            var doc = new XmlDocument();
            doc.LoadXml(File.ReadAllText(xmlfile));
            return doc;
        }

        public static void CopyDirectory(
            string src,
            string dst,
            string[] ignore = null,
            string[] match = null,
            Action<object> progress = null)
        {

            // Get the subdirectories for the specified directory.
            DirectoryInfo dir = new DirectoryInfo(src);

            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException(
                    "Source directory does not exist or could not be found: "
                    + src);
            }

            DirectoryInfo[] dirs = dir.GetDirectories();
            if (match != null)
            {
                dirs = dirs.Where(info =>
                {
                    return match.Any(pattern => new WildcardPattern(pattern).IsMatch(info.Name));
                }).ToArray();
            }
            if (ignore != null)
            {
                dirs = dirs.Where(info =>
                {
                    return !ignore.Any(pattern => new WildcardPattern(pattern).IsMatch(info.Name));
                }).ToArray();
            }
            // If the destination directory doesn't exist, create it.
            if (!Directory.Exists(dst))
            {
                Directory.CreateDirectory(dst);
            }

            // Get the files in the directory and copy them to the new location.
            FileInfo[] files = dir.GetFiles();

            if (match != null)
            {
                files = files.Where(info =>
                {
                    return match.Any(pattern => new WildcardPattern(pattern).IsMatch(info.Name));
                }).ToArray();
            }
            if (ignore != null)
            {
                files = files.Where(info =>
                {
                    return !ignore.Any(pattern => new WildcardPattern(pattern).IsMatch(info.Name));
                }).ToArray();
            }

            foreach (FileInfo file in files)
            {
                string temppath = Path.Combine(dst, file.Name);
                progress?.Invoke(file);
                file.CopyTo(temppath, true);
            }

            // If copying subdirectories, copy them and their contents to new location.
            foreach (DirectoryInfo subdir in dirs)
            {
                string temppath = Path.Combine(dst, subdir.Name);
                progress?.Invoke(subdir);
                CopyDirectory(subdir.FullName, temppath, ignore, match, progress);
            }
        }

        public static void CloneGit(string srcrepo, string dstdir, bool submodules = false, string branch = "")
        {
            var options = new CloneOptions
            {
                RepositoryOperationStarting = context =>
                {
                    Console.WriteLine("Cloning: " + context.RepositoryPath);
                    if (!string.IsNullOrEmpty(context.SubmoduleName))
                    {
                        Console.WriteLine("From submodule " + context.SubmoduleName);
                    }
                    Console.WriteLine("");
                    return true;
                },
                RepositoryOperationCompleted = context =>
                {
                    Console.WriteLine("Done: " + context.RepositoryPath);
                    if (!string.IsNullOrEmpty(context.SubmoduleName))
                    {
                        Console.WriteLine("a.k.a " + context.SubmoduleName);
                    }
                },
                OnCheckoutProgress = (path, steps, totalSteps) =>
                {
                    Console.Write("\rChecking out: {0} / {1}", steps, totalSteps);
                },
                OnTransferProgress = progress =>
                {
                    Console.Write("\rRecieving: {0} / {1} ({2} kb)",
                        progress.IndexedObjects,
                        progress.TotalObjects,
                        progress.ReceivedBytes/1024);
                    return true;
                },
                RecurseSubmodules = submodules
            };
            if (!string.IsNullOrEmpty(branch)) options.BranchName = branch;
            Repository.Clone(srcrepo, dstdir, options);
            Console.WriteLine("Done");
        }
    }
}
