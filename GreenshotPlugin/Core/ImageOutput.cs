﻿/*
 * Greenshot - a free and open source screenshot tool
 * Copyright (C) 2007-2012  Thomas Braun, Jens Klingen, Robin Krom
 * 
 * For more information see: http://getgreenshot.org/
 * The Greenshot project is hosted on Sourceforge: http://sourceforge.net/projects/greenshot/
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 1 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Greenshot.IniFile;
using Greenshot.Plugin;
using GreenshotPlugin.Controls;
using Greenshot.Core;

namespace GreenshotPlugin.Core {
	/// <summary>
	/// Description of ImageOutput.
	/// </summary>
	public static class ImageOutput {
		private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(typeof(ImageOutput));
		private static CoreConfiguration conf = IniConfig.GetIniSection<CoreConfiguration>();
		private static readonly int PROPERTY_TAG_SOFTWARE_USED = 0x0131;
		private static Cache<string, string> tmpFileCache = new Cache<string, string>(10 * 60 * 60, new Cache<string, string>.CacheObjectExpired(RemoveExpiredTmpFile));

		/// <summary>
		/// Creates a PropertyItem (Metadata) to store with the image.
		/// For the possible ID's see: http://msdn.microsoft.com/de-de/library/system.drawing.imaging.propertyitem.id(v=vs.80).aspx
		/// This code uses Reflection to create a PropertyItem, although it's not adviced it's not as stupid as having a image in the project so we can read a PropertyItem from that!
		/// </summary>
		/// <param name="id">ID</param>
		/// <param name="text">Text</param>
		/// <returns></returns>
		private static PropertyItem CreatePropertyItem(int id, string text) {
			PropertyItem propertyItem = null;
			try {
				ConstructorInfo ci = typeof(PropertyItem).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public, null, new Type[] { }, null);
				propertyItem = (PropertyItem)ci.Invoke(null);
				// Make sure it's of type string
				propertyItem.Type = 2;
				// Set the ID
				propertyItem.Id = id;
				// Set the text
				byte[] byteString = System.Text.ASCIIEncoding.ASCII.GetBytes(text + " ");
				// Set Zero byte for String end.
				byteString[byteString.Length - 1] = 0;
				propertyItem.Value = byteString;
				propertyItem.Len = text.Length + 1;
			} catch (Exception e) {
				LOG.WarnFormat("Error creating a PropertyItem: {0}", e.Message);
			}
			return propertyItem;
		}
		#region save

		/// <summary>
		/// Saves image to stream with specified quality
		/// To prevent problems with GDI version of before Windows 7:
		/// the stream is checked if it's seekable and if needed a MemoryStream as "cache" is used.
		/// </summary>
		public static void SaveToStream(ISurface surface, Stream stream, SurfaceOutputSettings outputSettings) {
			ImageFormat imageFormat = null;
			bool disposeImage = false;
			bool useMemoryStream = false;
			MemoryStream memoryStream = null;

			switch (outputSettings.Format) {
				case OutputFormat.bmp:
					imageFormat = ImageFormat.Bmp;
					break;
				case OutputFormat.gif:
					imageFormat = ImageFormat.Gif;
					break;
				case OutputFormat.jpg:
					imageFormat = ImageFormat.Jpeg;
					break;
				case OutputFormat.tiff:
					imageFormat = ImageFormat.Tiff;
					break;
				case OutputFormat.greenshot:
				case OutputFormat.png:
				default:
					// Problem with non-seekable streams most likely doesn't happen with Windows 7 (OS Version 6.1 and later)
					// http://stackoverflow.com/questions/8349260/generic-gdi-error-on-one-machine-but-not-the-other
					if (!stream.CanSeek) {
						int majorVersion = Environment.OSVersion.Version.Major;
						int minorVersion = Environment.OSVersion.Version.Minor;
						if (majorVersion < 6 || (majorVersion == 6 && minorVersion == 0)) {
							useMemoryStream = true;
							LOG.Warn("Using memorystream prevent an issue with saving to a non seekable stream.");
						}
					}
					imageFormat = ImageFormat.Png;
					break;
			}

			// check what image we want to save
			Image imageToSave = null;
			if (outputSettings.Format == OutputFormat.greenshot || outputSettings.SaveBackgroundOnly) {
				// We save the image of the surface, this should not be disposed
				imageToSave = surface.Image;
			} else {
				// We create the export image of the surface to save
				imageToSave = surface.GetImageForExport();
				disposeImage = true;
			}
			try {

				// apply effects, if there are any
				Point ignoreOffset;
				Image tmpImage = ImageHelper.ApplyEffects((Bitmap)imageToSave, outputSettings.Effects, out ignoreOffset);
				if (tmpImage != null) {
					if (disposeImage) {
						imageToSave.Dispose();
					}
					imageToSave = tmpImage;
					disposeImage = true;
				}

				// Removing transparency if it's not supported in the output
				if (imageFormat != ImageFormat.Png && Image.IsAlphaPixelFormat(imageToSave.PixelFormat)) {
					Image nonAlphaImage = ImageHelper.Clone(imageToSave, PixelFormat.Format24bppRgb);
					if (disposeImage) {
						imageToSave.Dispose();
					}
					// Make sure the image is disposed!
					disposeImage = true;
					imageToSave = nonAlphaImage;
				}

				// check for color reduction, forced or automatically
				if (conf.OutputFileAutoReduceColors || outputSettings.ReduceColors) {
					WuQuantizer quantizer = new WuQuantizer((Bitmap)imageToSave);
					int colorCount = quantizer.GetColorCount();
					LOG.InfoFormat("Image with format {0} has {1} colors", imageToSave.PixelFormat, colorCount);
					if (outputSettings.ReduceColors || colorCount < 256) {
						try {
							LOG.Info("Reducing colors on bitmap to 255.");
							tmpImage = quantizer.GetQuantizedImage(255);
							if (disposeImage) {
								imageToSave.Dispose();
							}
							imageToSave = tmpImage;
							// Make sure the "new" image is disposed
							disposeImage = true;
						} catch (Exception e) {
							LOG.Warn("Error occurred while Quantizing the image, ignoring and using original. Error: ", e);
						}
					}
				}

				// Create meta-data
				PropertyItem softwareUsedPropertyItem = CreatePropertyItem(PROPERTY_TAG_SOFTWARE_USED, "Greenshot");
				if (softwareUsedPropertyItem != null) {
					try {
						imageToSave.SetPropertyItem(softwareUsedPropertyItem);
					} catch (ArgumentException) {
						LOG.WarnFormat("Image of type {0} do not support property {1}", imageFormat, softwareUsedPropertyItem.Id);
					}
				}
				LOG.DebugFormat("Saving image to stream with Format {0} and PixelFormat {1}", imageFormat, imageToSave.PixelFormat);

				// Check if we want to use a memory stream, to prevent a issue which happens with Windows before "7".
				// The save is made to the targetStream, this is directed to either the MemoryStream or the original
				Stream targetStream = stream;
				if (useMemoryStream) {
					memoryStream = new MemoryStream();
					targetStream = memoryStream;
				}

				if (imageFormat == ImageFormat.Jpeg) {
					bool foundEncoder = false;
					foreach (ImageCodecInfo imageCodec in ImageCodecInfo.GetImageEncoders()) {
						if (imageCodec.FormatID == imageFormat.Guid) {
							EncoderParameters parameters = new EncoderParameters(1);
							parameters.Param[0] = new EncoderParameter(Encoder.Quality, outputSettings.JPGQuality);
							imageToSave.Save(targetStream, imageCodec, parameters);
							foundEncoder = true;
							break;
						}
					}
					if (!foundEncoder) {
						throw new ApplicationException("No JPG encoder found, this should not happen.");
					}
				} else {
					imageToSave.Save(targetStream, imageFormat);
				}

				// If we used a memory stream, we need to stream the memory stream to the original stream.
				if (useMemoryStream) {
					memoryStream.WriteTo(stream);
				}
				// Output the surface elements, size and marker to the stream
				if (outputSettings.Format == OutputFormat.greenshot) {
					using (MemoryStream tmpStream = new MemoryStream()) {
						long bytesWritten = surface.SaveElementsToStream(tmpStream);
						using (BinaryWriter writer = new BinaryWriter(tmpStream)) {
							writer.Write(bytesWritten);
							Version v = Assembly.GetExecutingAssembly().GetName().Version;
							byte[] marker = System.Text.Encoding.ASCII.GetBytes(String.Format("Greenshot{0:00}.{1:00}", v.Major, v.Minor));
							writer.Write(marker);
							tmpStream.WriteTo(stream);
						}
					}
				}
			} finally {
				if (memoryStream != null) {
					memoryStream.Dispose();
				}
				// cleanup if needed
				if (disposeImage && imageToSave != null) {
					imageToSave.Dispose();
				}
			}
		}

		/// <summary>
		/// Load a Greenshot surface
		/// </summary>
		/// <param name="fullPath"></param>
		/// <returns></returns>
		public static ISurface LoadGreenshotSurface(string fullPath, ISurface returnSurface) {
			if (string.IsNullOrEmpty(fullPath)) {
				return null;
			}
			Bitmap fileBitmap = null;
			LOG.InfoFormat("Loading image from file {0}", fullPath);
			// Fixed lock problem Bug #3431881
			using (Stream surfaceFileStream = File.OpenRead(fullPath)) {
				// And fixed problem that the bitmap stream is disposed... by Cloning the image
				// This also ensures the bitmap is correctly created

				// We create a copy of the bitmap, so everything else can be disposed
				surfaceFileStream.Position = 0;
				using (Image tmpImage = Image.FromStream(surfaceFileStream, true, true)) {
					LOG.DebugFormat("Loaded {0} with Size {1}x{2} and PixelFormat {3}", fullPath, tmpImage.Width, tmpImage.Height, tmpImage.PixelFormat);
					fileBitmap = ImageHelper.Clone(tmpImage);
				}
				// Start at -14 read "GreenshotXX.YY" (XX=Major, YY=Minor)
				const int markerSize = 14;
				surfaceFileStream.Seek(-markerSize, SeekOrigin.End);
				string greenshotMarker;
				using (StreamReader streamReader = new StreamReader(surfaceFileStream)) {
					greenshotMarker = streamReader.ReadToEnd();
					if (greenshotMarker == null || !greenshotMarker.StartsWith("Greenshot")) {
						throw new ArgumentException(string.Format("{0} is not a Greenshot file!", fullPath));
					}
					LOG.InfoFormat("Greenshot file format: {0}", greenshotMarker);
					const int filesizeLocation = 8 + markerSize;
					surfaceFileStream.Seek(-filesizeLocation, SeekOrigin.End);
					long bytesWritten = 0;
					using (BinaryReader reader = new BinaryReader(surfaceFileStream)) {
						bytesWritten = reader.ReadInt64();
						surfaceFileStream.Seek(-(bytesWritten + filesizeLocation), SeekOrigin.End);
						returnSurface.LoadElementsFromStream(surfaceFileStream);
					}
				}
			}
			if (fileBitmap != null) {
				returnSurface.Image = fileBitmap;
				LOG.InfoFormat("Information about file {0}: {1}x{2}-{3} Resolution {4}x{5}", fullPath, fileBitmap.Width, fileBitmap.Height, fileBitmap.PixelFormat, fileBitmap.HorizontalResolution, fileBitmap.VerticalResolution);
			}
			return returnSurface;
		}

		/// <summary>
		/// Saves image to specific path with specified quality
		/// </summary>
		public static void Save(ISurface surface, string fullPath, bool allowOverwrite, SurfaceOutputSettings outputSettings, bool copyPathToClipboard) {
			fullPath = FilenameHelper.MakeFQFilenameSafe(fullPath);
			string path = Path.GetDirectoryName(fullPath);

			// check whether path exists - if not create it
			DirectoryInfo di = new DirectoryInfo(path);
			if (!di.Exists) {
				Directory.CreateDirectory(di.FullName);
			}

			if (!allowOverwrite && File.Exists(fullPath)) {
				ArgumentException throwingException = new ArgumentException("File '" + fullPath + "' already exists.");
				throwingException.Data.Add("fullPath", fullPath);
				throw throwingException;
			}
			LOG.DebugFormat("Saving surface to {0}", fullPath);
			// Create the stream and call SaveToStream
			using (FileStream stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write)) {
				SaveToStream(surface, stream, outputSettings);
			}

			if (copyPathToClipboard) {
				ClipboardHelper.SetClipboardData(fullPath);
			}
		}

		/// <summary>
		/// Get the OutputFormat for a filename
		/// </summary>
		/// <param name="fullPath">filename (can be a complete path)</param>
		/// <returns>OutputFormat</returns>
		public static OutputFormat FormatForFilename(string fullPath) {
			// Fix for bug 2912959
			string extension = fullPath.Substring(fullPath.LastIndexOf(".") + 1);
			OutputFormat format = OutputFormat.png;
			try {
				if (extension != null) {
					format = (OutputFormat)Enum.Parse(typeof(OutputFormat), extension.ToLower());
				}
			} catch (ArgumentException ae) {
				LOG.Warn("Couldn't parse extension: " + extension, ae);
			}
			return format;
		}
		#endregion

		#region save-as

		/// <summary>
		/// Save with showing a dialog
		/// </summary>
		/// <param name="surface"></param>
		/// <param name="captureDetails"></param>
		/// <returns>Path to filename</returns>
		public static string SaveWithDialog(ISurface surface, ICaptureDetails captureDetails) {
			string returnValue = null;
			SaveImageFileDialog saveImageFileDialog = new SaveImageFileDialog(captureDetails);
			DialogResult dialogResult = saveImageFileDialog.ShowDialog();
			if (dialogResult.Equals(DialogResult.OK)) {
				try {
					string fileNameWithExtension = saveImageFileDialog.FileNameWithExtension;
					SurfaceOutputSettings outputSettings = new SurfaceOutputSettings(FormatForFilename(fileNameWithExtension));
					if (conf.OutputFilePromptQuality) {
						QualityDialog qualityDialog = new QualityDialog(outputSettings);
						qualityDialog.ShowDialog();
					}
					// TODO: For now we always overwrite, should be changed
					ImageOutput.Save(surface, fileNameWithExtension, true, outputSettings, conf.OutputFileCopyPathToClipboard);
					returnValue = fileNameWithExtension;
					conf.OutputFileAsFullpath = fileNameWithExtension;
					IniConfig.Save();
				} catch (System.Runtime.InteropServices.ExternalException) {
					MessageBox.Show(Language.GetFormattedString("error_nowriteaccess", saveImageFileDialog.FileName).Replace(@"\\", @"\"), Language.GetString("error"));
				}
			}
			return returnValue;
		}
		#endregion

		/// <summary>
		/// Create a tmpfile which has the name like in the configured pattern.
		/// Used e.g. by the email export
		/// </summary>
		/// <param name="surface"></param>
		/// <param name="captureDetails"></param>
		/// <param name="outputSettings"></param>
		/// <returns>Path to image file</returns>
		public static string SaveNamedTmpFile(ISurface surface, ICaptureDetails captureDetails, SurfaceOutputSettings outputSettings) {
			string pattern = conf.OutputFileFilenamePattern;
			if (pattern == null || string.IsNullOrEmpty(pattern.Trim())) {
				pattern = "greenshot ${capturetime}";
			}
			string filename = FilenameHelper.GetFilenameFromPattern(pattern, outputSettings.Format, captureDetails);
			// Prevent problems with "other characters", which causes a problem in e.g. Outlook 2007 or break our HTML
			filename = Regex.Replace(filename, @"[^\d\w\.]", "_");
			// Remove multiple "_"
			filename = Regex.Replace(filename, @"_+", "_");
			string tmpFile = Path.Combine(Path.GetTempPath(), filename);

			LOG.Debug("Creating TMP File: " + tmpFile);

			// Catching any exception to prevent that the user can't write in the directory.
			// This is done for e.g. bugs #2974608, #2963943, #2816163, #2795317, #2789218
			try {
				ImageOutput.Save(surface, tmpFile, true, outputSettings, false);
				tmpFileCache.Add(tmpFile, tmpFile);
			} catch (Exception e) {
				// Show the problem
				MessageBox.Show(e.Message, "Error");
				// when save failed we present a SaveWithDialog
				tmpFile = ImageOutput.SaveWithDialog(surface, captureDetails);
			}
			return tmpFile;
		}

		/// <summary>
		/// Helper method to create a temp image file
		/// </summary>
		/// <param name="image"></param>
		/// <returns></returns>
		public static string SaveToTmpFile(ISurface surface, SurfaceOutputSettings outputSettings, string destinationPath) {
			string tmpFile = Path.GetRandomFileName() + "." + outputSettings.Format.ToString();
			// Prevent problems with "other characters", which could cause problems
			tmpFile = Regex.Replace(tmpFile, @"[^\d\w\.]", "");
			if (destinationPath == null) {
				destinationPath = Path.GetTempPath();
			}
			string tmpPath = Path.Combine(destinationPath, tmpFile);
			LOG.Debug("Creating TMP File : " + tmpPath);

			try {
				ImageOutput.Save(surface, tmpPath, true, outputSettings, false);
				tmpFileCache.Add(tmpPath, tmpPath);
			} catch (Exception) {
				return null;
			}
			return tmpPath;
		}

		/// <summary>
		/// Cleanup all created tmpfiles
		/// </summary>	
		public static void RemoveTmpFiles() {
			foreach (string tmpFile in tmpFileCache.Elements) {
				if (File.Exists(tmpFile)) {
					LOG.DebugFormat("Removing old temp file {0}", tmpFile);
					File.Delete(tmpFile);
				}
				tmpFileCache.Remove(tmpFile);
			}
		}

		/// <summary>
		/// Cleanup handler for expired tempfiles
		/// </summary>
		/// <param name="filename"></param>
		/// <param name="alsoTheFilename"></param>
		private static void RemoveExpiredTmpFile(string filekey, object filename) {
			string path = filename as string;
			if (path != null && File.Exists(path)) {
				LOG.DebugFormat("Removing expired file {0}", path);
				File.Delete(path);
			}
		}
	}
}
