//Pardon my procedural programming. :)

using System;
using System.IO;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Threading;

namespace ClassAct
{
    public partial class Form1 : Form
    {
        private const string settingsFileName = "ClassAct.config";
        private const int undoLimit = 3;
        private string src = "", settingsFile = "";
        private string[] paths = new string[9];
        private string shownFile = "", lastShownFile = "";
        private bool allActionsTaken = false;
        private List<string> fileEnumerator;
        private List<Tuple<Image, string>> imageBuffer = new List<Tuple<Image, string>>();
        private List<Tuple<string, string>> history = new List<Tuple<string, string>>();
        private bool waitingForImage = false; //Set if the UI thread runs out of images but needs one; the image buffering thread deals with it then.
        private Thread imageBufferThread;
        private const string imageNotReadyText = " Loading the next image...";
        private const string actionAlreadyTakenText = "Action already taken on the displayed image. Please wait for the next image to load.";

        public Form1()
        {
            InitializeComponent();
        }

        public Form1(string directory)
        {
            //If a filename was specified and a directory with the same name doesn't exist, remove the file name
            if (File.Exists(directory) && !Directory.Exists(directory)) directory = Path.GetDirectoryName(directory);
            //Make sure the path is absolute
            src = Path.GetFullPath(directory);
            InitializeComponent();
        }

        private string pickFolder()
        {
            var dlg = folderBrowserDialog1.ShowDialog();
            if (dlg == DialogResult.Cancel)
            {
                lblFeedback.Text = "Folder selection canceled";
                return "";
            }
            else
            {
                return folderBrowserDialog1.SelectedPath;
            }
        }

        private void Form1_Shown(object sender, EventArgs e)
        {
            if (src == "" || !Directory.Exists(src))
            {
                //Pick a source folder
                src = pickFolder();
                if (src == "")
                {
                    Application.Exit();
                    return;
                }
            }

            fileEnumerator = Directory.EnumerateFiles(src).ToList();

            //Check for a saved settings file
            settingsFile = Path.Combine(src, settingsFileName);
            if (File.Exists(settingsFile))
            {
                var loadedPaths = File.ReadAllLines(settingsFile);
                //Only load the saved paths that still exist
                for (int x = 0; x < loadedPaths.Length && x < paths.Length; x++)
                {
                    if (Directory.Exists(loadedPaths[x])) paths[x] = loadedPaths[x];
                }
                lblFeedback.Text = "Settings restored. Press F1 for help" + getRemainingFileCountStringToAppend();
            }
            else
            {
                lblFeedback.Text = "File list prepared. Press F1 for help" + getRemainingFileCountStringToAppend();
            }

            //Start off
            nextFile();

            //Start a second thread for loading images
            imageBufferThread = new Thread(maintainImageBuffer);
            imageBufferThread.Start();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            preExit();
        }

        private bool takeImage()
        {
            if (pictureBox1.Image != null) pictureBox1.Image.Dispose();
            lastShownFile = shownFile;
            lock (imageBuffer) //Make sure the other thread isn't in the process of adding an image to the buffer
            {
                if (imageBuffer.Count != 0)
                {
                    pictureBox1.Image = imageBuffer.First().Item1;
                    shownFile = imageBuffer.First().Item2;
                    imageBuffer.RemoveAt(0);
                    pictureBox1.Refresh();
                    Application.DoEvents();
                    allActionsTaken = false; //If we just loaded a new file, then the user obviously hasn't taken action on all the files yet

                    //Keep the user posted when the image finishes loading, if they were previously informed that the next image wasn't ready yet
                    if (lblFeedback.Text.EndsWith(imageNotReadyText)) lblFeedback.Text = lblFeedback.Text.Substring(0, lblFeedback.Text.Length - imageNotReadyText.Length);
                    else if (lblFeedback.Text == actionAlreadyTakenText) lblFeedback.Text = "Image loaded" + getRemainingFileCountStringToAppend();

                    return true; //Image was ready
                }
                if (fileEnumerator.Count == 0)
                {
                    pictureBox1.Image = new Bitmap(10, 10);
                    lblFeedback.Text = "Reached last file";
                    allActionsTaken = true;
                }
                //If we didn't have an image ready to display, let the user know
                if (imageBuffer.Count == 0) lblFeedback.Text += imageNotReadyText;
            }
            return false; //Image was not ready
        }

        private void nextFile()
        {
            //If there are no images ready, notify other thread that the UI thread has delegated responsibility to it for displaying an image
            waitingForImage = !takeImage();
        }

        //Thread 2
        private void maintainImageBuffer()
        {
            while (true)
            {
                if (imageBuffer.Count < 3) //Don't need to lock when only reading the Count; the other thread can only subtract from it.
                {
                    do //Don't sleep until you have at least one image; otherwise, you could be sleeping 20ms between a bunch of non-image files, making the user wait unnecessarily.
                    {
                        var file = fileEnumerator.LastOrDefault();
                        if (string.IsNullOrEmpty(file))
                        {
                            return; //No more; don't loop forever
                        }
                        else
                        {
                            try
                            {
                                using (var fs = new FileStream(file, FileMode.Open))
                                {
                                    //Don't load files over 50 MiB because they're probably not in a format we can load anyway.
                                    if (fs.Length <= 50 * 1024 * 1024)
                                    {
                                        var newImage = new Bitmap(fs);
                                        lock (imageBuffer) //Minimum possible lock time
                                        {
                                            imageBuffer.Add(new Tuple<Image, string>(newImage, file));
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                //File is just skipped. Nothing to do.
                            }
                            fileEnumerator.RemoveAt(fileEnumerator.Count - 1);
                        }
                    } while (imageBuffer.Count < 1); //Don't sleep until you have at least one image
                }

                //If we didn't have an image shown already but we have one that we can show, show one
                if (waitingForImage && imageBuffer.Count != 0)
                {
                    //Use the UI thread
                    this.Invoke(new Action(() => {
                        waitingForImage = !takeImage();
                    }));
                }

                Thread.Sleep(20);
            }
        }

        private string getRemainingFileCountStringToAppend()
        {
            return ". " + (imageBuffer.Count + fileEnumerator.Count) + " remaining files.";
        }

        private void undoPurge(bool all = false)
        {
            var target = (all ? 0 : undoLimit);
            while (history.Count > target)
            {
                //Check if it was a tentative deletion. If so, finally delete the file as requested.
                if (history.First().Item2 == null)
                {
                    try
                    {
                        File.Delete(history.First().Item1);
                    }
                    catch { } //Tentatively no error reporting
                }
                //Remove the oldest entry from the history
                history.RemoveAt(0);
            }
        }

        private void moveWithUndo(string src, string dest)
        {
            try
            {
                File.Move(src, dest);
                history.Add(new Tuple<string, string>(src, dest));
                undoPurge();
                lblFeedback.Text = "Moved" + getRemainingFileCountStringToAppend();
            }
            catch { }
        }

        private void deleteWithUndo(string src)
        {
            history.Add(new Tuple<string, string>(src, null));
            undoPurge();

            lblFeedback.Text = "Deleted" + getRemainingFileCountStringToAppend();
        }

        private void actualUndo()
        {
            if (history.Count == 0)
            {
                lblFeedback.Text = "Cannot undo any further" + getRemainingFileCountStringToAppend();
                return; //Nothing to do if the history is empty
            }
            var toUndo = history.LastOrDefault();

            //If it wasn't a delete action, undo the action for real
            if (toUndo.Item2 != null)
            {
                try
                {
                    File.Move(toUndo.Item2, toUndo.Item1);
                }
                catch { }
            }

            //Put the item back on the file list for the near future so the user can decide again
            fileEnumerator.Add(toUndo.Item1);

            //Remove the most recent undo item from the history
            history.RemoveAt(history.Count - 1);

            //If the user already picked an action for the final file, show the user this image again
            if (imageBuffer.Count == 0 && allActionsTaken) nextFile();

            lblFeedback.Text = "Undid " + (toUndo.Item2 == null ? "delete" : "move") + getRemainingFileCountStringToAppend();
        }

        private Tuple<Rectangle, Rectangle> calculateDualImageDisplayRectangles(Rectangle drawArea, Rectangle leftImage, Rectangle rightImage)
        {
            var midpoint = (float)(drawArea.Right - drawArea.Left) / 2;

            var lScale = Math.Min((float)midpoint / leftImage.Width, (float)drawArea.Height / leftImage.Height);
            var lw = leftImage.Width * lScale;
            var lh = leftImage.Height * lScale;
            var leftIsLimitedByHeight = Math.Abs(lh - drawArea.Height) < 1;

            var rScale = Math.Min((float)(drawArea.Width - midpoint) / rightImage.Width, (float)drawArea.Height / rightImage.Height);
            var rw = rightImage.Width * rScale;
            var rh = rightImage.Height * rScale;
            var rightIsLimitedByHeight = Math.Abs(rh - drawArea.Height) < 1;

            //If either is limited by height, but not both, we can adjust the midpoint to grant extra width to the one that is limited by width
            if (leftIsLimitedByHeight && !rightIsLimitedByHeight)
            {
                midpoint = lw;
                //Recalculate the right (exact same 3 lines of code as before)
                rScale = Math.Min((float)(drawArea.Width - midpoint) / rightImage.Width, (float)drawArea.Height / rightImage.Height);
                rw = rightImage.Width * rScale;
                rh = rightImage.Height * rScale;
            }
            else if (!leftIsLimitedByHeight && rightIsLimitedByHeight)
            {
                midpoint = drawArea.Width - rw;
                //Recalculate the left (exact same 3 lines of code as before)
                lScale = Math.Min((float)midpoint / leftImage.Width, (float)drawArea.Height / leftImage.Height);
                lw = leftImage.Width * lScale;
                lh = leftImage.Height * lScale;
            }

            return new Tuple<Rectangle, Rectangle>(new Rectangle(drawArea.Left + (int)(midpoint - lw) / 2, drawArea.Top + (int)(drawArea.Height - lh) / 2, (int)lw, (int)lh),
                new Rectangle(drawArea.Left + (int)(drawArea.Width + midpoint - rw) / 2, drawArea.Top + (int)(drawArea.Height - rh) / 2, (int)rw, (int)rh));
        }

        /// <summary>
        /// Move the currently shown file to the given path
        /// </summary>
        /// <param name="destination">Destination. If empty, allow user to pick.</param>
        /// <returns>The same path as passed in, or if it was empty, the path the user picked (canceled = also empty)</returns>
        private string moveAndAdvance(string destination)
        {
            if (String.IsNullOrEmpty(destination))
            {
                lblFeedback.Text = "Select a destination folder for this key.";
                destination = pickFolder();
            }
            if (!String.IsNullOrEmpty(destination))
            {
                var destFilename = destination + Path.DirectorySeparatorChar + Path.GetFileName(shownFile);
                var requestDelete = false;

                //If both files exist, it interrupts the flow a bit, but ask the user what to do.
                if (File.Exists(destFilename) && File.Exists(shownFile))
                {
                    //Display both images for the user, side by side, so they can see if they're the same
                    var imageWas = pictureBox1.Image;
                    pictureBox1.Image = new Bitmap(pictureBox1.Width, pictureBox1.Height);
                    var g = Graphics.FromImage(pictureBox1.Image);
                    try
                    {
                        var otherImage = Image.FromFile(destFilename);
                        //Draw imageWas and otherImage side-by-side within pictureBox1.Image
                        var rects = calculateDualImageDisplayRectangles(pictureBox1.DisplayRectangle, new Rectangle(new Point(0, 0), imageWas.Size), new Rectangle(new Point(0, 0), otherImage.Size));
                        g.DrawImage(imageWas, rects.Item1);
                        g.DrawImage(otherImage, rects.Item2);
                        lblFeedback.Text = "Source image is shown on left; image with same name in destination is shown on right.";
                    }
                    catch
                    {
                        g.FillRectangle(new SolidBrush(Color.FromArgb(160, 0, 0, 0)), pictureBox1.DisplayRectangle);
                        g.DrawString("Destination file could not be loaded as an image", new Font(this.Font.FontFamily, 26), Brushes.White, pictureBox1.DisplayRectangle);
                        lblFeedback.Text = "Destination file could not be loaded as an image";
                    }
                    g.Dispose();
                    pictureBox1.Refresh();
                    Application.DoEvents();
                    
                    var action = MessageBox.Show("Duplicate filename (" + Path.GetFileName(shownFile) + "). Delete this copy? Select No to keep both.", "ClassAct - Duplicate File", MessageBoxButtons.YesNoCancel);
                    if (action == DialogResult.Yes) requestDelete = true;
                    else if (action == DialogResult.No)
                    {
                        //Copy with different name
                        var extension = Path.GetExtension(destFilename);
                        var baseName = destFilename.Substring(0, destFilename.Length - extension.Length);
                        for (int x = 1; x < 1000 && File.Exists(destFilename); x++)
                        {
                            destFilename = baseName + " (" + x + ")" + extension;
                        }
                    }
                    else if (action == DialogResult.Cancel)
                    {
                        //Don't take the action requested.
                        lblFeedback.Text = "Action canceled" + getRemainingFileCountStringToAppend();
                        //Clean up the temporary image and reset the picture box to the old image so it can be disposed properly
                        pictureBox1.Image.Dispose();
                        pictureBox1.Image = imageWas;
                        return destination;
                    }
                    //Clean up the temporary image and reset the picture box to the old image so it can be disposed properly
                    pictureBox1.Image.Dispose();
                    pictureBox1.Image = imageWas;
                }

                nextFile();
                try
                {
                    if (requestDelete) deleteWithUndo(lastShownFile);
                    else
                    {
                        moveWithUndo(lastShownFile, destFilename);
                    }
                }
                catch
                {
                    //No error reporting at this time.
                }
            }
            return destination;
        }

        private void preExit()
        {
            lblFeedback.Text = "Purging undo history and saving destination directory settings...";

            //Exit the image buffering thread
            if (imageBufferThread != null) imageBufferThread.Abort();

            //Purge the undo queue (tentatively deleted files will be actually deleted)
            undoPurge(true);

            if (String.IsNullOrEmpty(settingsFile)) return;

            //Save the current setup
            File.WriteAllLines(settingsFile, paths);
        }

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            //If we're waiting for the next image to be loaded, don't let the user try to do something to the currently displayed image, because they already did something to it.
            if (waitingForImage && e.KeyCode != Keys.Subtract && e.KeyCode != Keys.Escape && e.KeyCode != Keys.F1)
            {
                lblFeedback.Text = actionAlreadyTakenText;
                return;
            }

            var index = -1;
            if (e.KeyCode == Keys.NumPad1) index = 0;
            else if (e.KeyCode == Keys.NumPad2) index = 1;
            else if (e.KeyCode == Keys.NumPad3) index = 2;
            else if (e.KeyCode == Keys.NumPad4) index = 3;
            else if (e.KeyCode == Keys.NumPad5) index = 4;
            else if (e.KeyCode == Keys.NumPad6) index = 5;
            else if (e.KeyCode == Keys.NumPad7) index = 6;
            else if (e.KeyCode == Keys.NumPad8) index = 7;
            else if (e.KeyCode == Keys.NumPad9) index = 8;
            else if (e.KeyCode == Keys.NumPad0)
            {
                //Delete
                nextFile();
                deleteWithUndo(lastShownFile);
            }
            else if (e.KeyCode == Keys.Enter)
            {
                //Skip (push to end of queue)
                lblFeedback.Text = "File moved to end of queue" + getRemainingFileCountStringToAppend();
                fileEnumerator.Insert(0, shownFile);
                nextFile();
            }
            else if (e.KeyCode == Keys.Subtract)
            {
                //Undo
                actualUndo();
            }
            else if (e.KeyCode == Keys.F1)
            {
                lblFeedback.Text = "Numpad 1 through 9: move to folder\r\nControl + numpad 1 through 9: select new folder to move to\r\nNumpad 0: delete\r\nEnter: skip file\r\nNumpad minus: undo move or delete (up to " + undoLimit + " times)\r\nEscape: quit";
            }
            else if (e.KeyCode == Keys.Escape)
            {
                preExit();
                Application.Exit();
            }

            //If it was a number key
            if (index != -1)
            {
                if (e.Modifiers.HasFlag(Keys.LControlKey) || e.Modifiers.HasFlag(Keys.RControlKey)) paths[index] = ""; //Control allows resetting the path
                paths[index] = moveAndAdvance(paths[index]);
            }
        }
    }
}
