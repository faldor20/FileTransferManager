﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace IOExtensions
{
    public enum TransferResult { Success, Failed, Cancelled }

    public static class FileTransferManager
    {
        public static bool MoveWithProgress(string source, string destination, Action<TransferProgress> progress)
        {
            var startTimestamp = DateTime.Now;
            NativeMethods.CopyProgressRoutine lpProgressRoutine = (size, transferred, streamSize, bytesTransferred, number, reason, file, destinationFile, data) =>
            {
                TransferProgress fileProgress = new TransferProgress(startTimestamp, bytesTransferred)
                {
                    Total = size,
                    Transferred = transferred,
                    StreamSize = streamSize,
                    BytesTransferred = bytesTransferred,
                    ProcessedFile = source
                };
                try
                {
                    progress(fileProgress);
                    return NativeMethods.CopyProgressResult.PROGRESS_CONTINUE;
                }
                catch (Exception ex)
                {
                    return NativeMethods.CopyProgressResult.PROGRESS_STOP;
                }
            };
            if (!NativeMethods.MoveFileWithProgress(source, destination, lpProgressRoutine, IntPtr.Zero, NativeMethods.MoveFileFlags.MOVE_FILE_REPLACE_EXISTSING | NativeMethods.MoveFileFlags.MOVE_FILE_COPY_ALLOWED | NativeMethods.MoveFileFlags.MOVE_FILE_WRITE_THROUGH))
                return false;

            return true;
        }

        public static Task<bool> MoveWithProgressAsync(string source, string destination, Action<TransferProgress> progress)
        {
            return Task.Run(() =>
            {
                var destinationPathCorrected = destination;
                if (source.IsDirFile() == false)
                {
                    destinationPathCorrected = Helpers.CorrectFileDestinationPath(source, destination);
                }
                return MoveWithProgress(source, destinationPathCorrected, progress);
            });
        }

        public static Task<TransferResult> CopyWithProgressAsync(string source, string destination, Action<TransferProgress> progress, bool continueOnFailure)
        {
            return CopyWithProgressAsync(source, destination, progress, continueOnFailure, CancellationToken.None);
        }

        public static Task<TransferResult> CopyWithProgressAsync(string source, string destination, Action<TransferProgress> progress, bool continueOnFailure, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                try
                {
                    return CopyWithProgress(source, destination, progress, continueOnFailure, cancellationToken);
                }
                catch
                {
                    return TransferResult.Failed;
                }
            }, cancellationToken);
        }


        public static TransferResult CopyWithProgress(string source, string destination, Action<TransferProgress> progress, bool continueOnFailure)
        {
            return CopyWithProgress(source, destination, progress, continueOnFailure, CancellationToken.None);
        }

        public static TransferResult CopyWithProgress(string source, string destination, Action<TransferProgress> progress, bool continueOnFailure, CancellationToken cancellationToken)
        {
            var isDir = source.IsDirFile();
            if (isDir == null)
            {
                throw new ArgumentException("Source parameter has to be file or directory! " + source);
            }
            else if (isDir == true)
            {
                return CopyDirectoryWithProgress(source, destination, progress, continueOnFailure, cancellationToken);
            }
            else
            {
                if(cancellationToken.IsCancellationRequested)
                {
                    return TransferResult.Cancelled;
                }

                var destinationFile = Helpers.CorrectFileDestinationPath(source, destination);
                
                return CopyFileWithProgress(source, destinationFile, progress, cancellationToken);
            }
        }

        private static TransferResult CopyDirectoryWithProgress(string sourceDirectory, string destinationDirectory, Action<TransferProgress> progress, bool continueOnFailure, CancellationToken cancellationToken)
        {
            var rootSource = new DirectoryInfo(sourceDirectory);
            var rootSourceLength = rootSource.FullName.Length;
            var rootSourceSize = Helpers.DirSize(rootSource);
            long totalTransfered = 0;

            try
            {
                var destinationNewRootDir = Directory.CreateDirectory(Path.Combine(destinationDirectory, rootSource.Name));

                foreach (var directory in rootSource.EnumerateDirectories("*", SearchOption.AllDirectories))
                {
                    if(cancellationToken.IsCancellationRequested)
                    {
                        return TransferResult.Cancelled;
                    }
                    var newName = directory.FullName.Substring(rootSourceLength+1);
                    Directory.CreateDirectory(Path.Combine(destinationNewRootDir.FullName, newName));
                }

                foreach (var file in rootSource.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return TransferResult.Cancelled;
                    }

                    var newName = file.FullName.Substring(rootSourceLength+1);
                    var fileCopyStartTimestamp = DateTime.Now;
                    var result = CopyFileWithProgress(file.FullName, Path.Combine(destinationNewRootDir.FullName, newName), (partialProgress) =>
                    {
                        var totalProgress = new TransferProgress(fileCopyStartTimestamp, partialProgress.BytesTransferred)
                        {
                            Total = rootSourceSize.Size,
                            Transferred = totalTransfered + partialProgress.Transferred,
                            BytesTransferred = totalTransfered + partialProgress.Transferred,
                            StreamSize = rootSourceSize.Size,
                            ProcessedFile = file.FullName
                        };
                        progress(totalProgress);
                    }, cancellationToken);

                    if (result == TransferResult.Failed && !continueOnFailure)
                    {
                        return TransferResult.Failed;
                    }
                    else if(result == TransferResult.Cancelled)
                    {
                        return TransferResult.Cancelled;
                    }

                    totalTransfered += file.Length;
                }
            }
            catch (Exception ex)
            {
                return TransferResult.Failed;
            }
            return TransferResult.Success;
        }

        private static TransferResult CopyFileWithProgress(string sourceFile, string newFile, Action<TransferProgress> progress, CancellationToken cancellationToken)
        {
            int pbCancel = 0;
            var startTimestamp = DateTime.Now;
            NativeMethods.CopyProgressRoutine lpProgressRoutine = (size, transferred, streamSize, bytesTransferred, number, reason, file, destinationFile, data) =>
            {
                TransferProgress fileProgress = new TransferProgress(startTimestamp, bytesTransferred)
                {
                    Total = size,
                    Transferred = transferred,
                    StreamSize = streamSize,
                    ProcessedFile = sourceFile
                };
                try
                {
                    progress(fileProgress);
                    return NativeMethods.CopyProgressResult.PROGRESS_CONTINUE;
                }
                catch (Exception ex)
                {
                    return NativeMethods.CopyProgressResult.PROGRESS_STOP;
                }
            };
            if(cancellationToken.IsCancellationRequested)
            {
                return TransferResult.Cancelled;
            }
            
            var ctr = cancellationToken.Register(() => pbCancel = 1);

            var result = NativeMethods.CopyFileEx(sourceFile, newFile, lpProgressRoutine, IntPtr.Zero, ref pbCancel, NativeMethods.CopyFileFlags.COPY_FILE_RESTARTABLE);
            if(cancellationToken.IsCancellationRequested)
            {
                return TransferResult.Cancelled;
            }

            return result ? TransferResult.Success : TransferResult.Failed;
        }
    }
}
