using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector.Core;
using MySqlConnector.Logging;
using MySqlConnector.Protocol;
using MySqlConnector.Protocol.Serialization;
using MySqlConnector.Utilities;

namespace MySqlConnector
{
	public sealed class MySqlBulkCopy
	{
		public MySqlBulkCopy(MySqlConnection connection, MySqlTransaction? transaction = null)
		{
			m_connection = connection ?? throw new ArgumentNullException(nameof(connection));
			m_transaction = transaction;
			ColumnMappings = new List<MySqlBulkCopyColumnMapping>();
		}

		public int BulkCopyTimeout { get; set; }

		/// <summary>
		/// The name of the table to insert rows into.
		/// </summary>
		/// <remarks>The table name shouldn't be quoted or escaped.</remarks>
		public string? DestinationTableName { get; set; }

		/// <summary>
		/// Defines the number of rows to be processed before generating a notification event.
		/// </summary>
		public int NotifyAfter { get; set; }

		/// <summary>
		/// Occurs every time that the number of rows specified by the <see cref="NotifyAfter"/> property have been processed,
		/// and once after all rows have been copied (if <see cref="NotifyAfter"/> is non-zero).
		/// </summary>
		/// <remarks>
		/// Receipt of a RowsCopied event does not imply that any rows have been sent to the server or committed.
		/// </remarks>
		public event MySqlRowsCopiedEventHandler? MySqlRowsCopied;

		/// <summary>
		/// A collection of <see cref="MySqlBulkCopyColumnMapping"/> objects. If the columns being copied from the
		/// data source line up one-to-one with the columns in the destination table then populating this collection is
		/// unnecessary. Otherwise, this should be filled with a collection of <see cref="MySqlBulkCopyColumnMapping"/> objects
		/// specifying how source columns are to be mapped onto destination columns. If one column mapping is specified,
		/// then all must be specified.
		/// </summary>
		public List<MySqlBulkCopyColumnMapping> ColumnMappings { get; }

		/// <summary>
		/// Returns the number of rows that were copied (after <code>WriteToServer(Async)</code> finishes).
		/// </summary>
		public int RowsCopied { get; private set; }

#if !NETSTANDARD1_3
		public void WriteToServer(DataTable dataTable)
		{
			m_valuesEnumerator = DataRowsValuesEnumerator.Create(dataTable ?? throw new ArgumentNullException(nameof(dataTable)));
			WriteToServerAsync(IOBehavior.Synchronous, CancellationToken.None).GetAwaiter().GetResult();
		}

#if !NETSTANDARD2_1 && !NETCOREAPP3_0
		public async Task WriteToServerAsync(DataTable dataTable, CancellationToken cancellationToken = default)
		{
			m_valuesEnumerator = DataRowsValuesEnumerator.Create(dataTable ?? throw new ArgumentNullException(nameof(dataTable)));
			await WriteToServerAsync(IOBehavior.Asynchronous, cancellationToken).ConfigureAwait(false);
		}
#else
		public async ValueTask WriteToServerAsync(DataTable dataTable, CancellationToken cancellationToken = default)
		{
			m_valuesEnumerator = DataRowsValuesEnumerator.Create(dataTable ?? throw new ArgumentNullException(nameof(dataTable)));
			await WriteToServerAsync(IOBehavior.Asynchronous, cancellationToken).ConfigureAwait(false);
		}
#endif

		public void WriteToServer(IEnumerable<DataRow> dataRows, int columnCount)
		{
			m_valuesEnumerator = new DataRowsValuesEnumerator(dataRows ?? throw new ArgumentNullException(nameof(dataRows)), columnCount);
			WriteToServerAsync(IOBehavior.Synchronous, CancellationToken.None).GetAwaiter().GetResult();
		}
#if !NETSTANDARD2_1 && !NETCOREAPP3_0
		public async Task WriteToServerAsync(IEnumerable<DataRow> dataRows, int columnCount, CancellationToken cancellationToken = default)
		{
			m_valuesEnumerator = new DataRowsValuesEnumerator(dataRows ?? throw new ArgumentNullException(nameof(dataRows)), columnCount);
			await WriteToServerAsync(IOBehavior.Asynchronous, cancellationToken).ConfigureAwait(false);
		}
#else
		public async ValueTask WriteToServerAsync(IEnumerable<DataRow> dataRows, int columnCount, CancellationToken cancellationToken = default)
		{
			m_valuesEnumerator = new DataRowsValuesEnumerator(dataRows ?? throw new ArgumentNullException(nameof(dataRows)), columnCount);
			await WriteToServerAsync(IOBehavior.Asynchronous, cancellationToken).ConfigureAwait(false);
		}
#endif
#endif

		public void WriteToServer(IDataReader dataReader)
		{
			m_valuesEnumerator = DataReaderValuesEnumerator.Create(dataReader ?? throw new ArgumentNullException(nameof(dataReader)));
			WriteToServerAsync(IOBehavior.Synchronous, CancellationToken.None).GetAwaiter().GetResult();
		}
#if !NETSTANDARD2_1 && !NETCOREAPP3_0
		public async Task WriteToServerAsync(IDataReader dataReader, CancellationToken cancellationToken = default)
		{
			m_valuesEnumerator = DataReaderValuesEnumerator.Create(dataReader ?? throw new ArgumentNullException(nameof(dataReader)));
			await WriteToServerAsync(IOBehavior.Asynchronous, cancellationToken).ConfigureAwait(false);
		}
#else
		public async ValueTask WriteToServerAsync(IDataReader dataReader, CancellationToken cancellationToken = default)
		{
			m_valuesEnumerator = DataReaderValuesEnumerator.Create(dataReader ?? throw new ArgumentNullException(nameof(dataReader)));
			await WriteToServerAsync(IOBehavior.Asynchronous, cancellationToken).ConfigureAwait(false);
		}
#endif

#if !NETSTANDARD2_1 && !NETCOREAPP3_0
		private async ValueTask<int> WriteToServerAsync(IOBehavior ioBehavior, CancellationToken cancellationToken)
#else
		private async ValueTask WriteToServerAsync(IOBehavior ioBehavior, CancellationToken cancellationToken)
#endif
		{
			var tableName = DestinationTableName ?? throw new InvalidOperationException("DestinationTableName must be set before calling WriteToServer");
			m_wasAborted = false;

			Log.Info("Starting bulk copy to {0}", tableName);
			var bulkLoader = new MySqlBulkLoader(m_connection)
			{
				CharacterSet = "utf8mb4",
				EscapeCharacter = '\\',
				FieldQuotationCharacter = '\0',
				FieldTerminator = "\t",
				LinePrefix = null,
				LineTerminator = "\n",
				Local = true,
				NumberOfLinesToSkip = 0,
				Source = this,
				TableName = tableName,
				Timeout = BulkCopyTimeout,
			};

			var closeConnection = false;
			if (m_connection.State != ConnectionState.Open)
			{
				m_connection.Open();
				closeConnection = true;
			}

			// merge column mappings with the destination schema
			var columnMappings = new List<MySqlBulkCopyColumnMapping>(ColumnMappings);
			var addDefaultMappings = columnMappings.Count == 0;
			using (var cmd = new MySqlCommand("select * from " + tableName + ";", m_connection, m_transaction))
			using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ioBehavior, cancellationToken).ConfigureAwait(false))
			{
				var schema = reader.GetColumnSchema();
				for (var i = 0; i < Math.Min(m_valuesEnumerator!.FieldCount, schema.Count); i++)
				{
					var destinationColumn = reader.GetName(i);
					if (schema[i].DataTypeName == "BIT")
					{
						AddColumnMapping(columnMappings, addDefaultMappings, i, destinationColumn, $"@`\uE002\bcol{i}`", $"%COL% = CAST(%VAR% AS UNSIGNED)");
					}
					else if (schema[i].DataTypeName == "YEAR")
					{
						// the current code can't distinguish between 0 = 0000 and 0 = 2000
						throw new NotSupportedException("'YEAR' columns are not supported by MySqlBulkLoader.");
					}
					else
					{
						var type = schema[i].DataType;
						if (type == typeof(byte[]) || (type == typeof(Guid) && (m_connection.GuidFormat == MySqlGuidFormat.Binary16 || m_connection.GuidFormat == MySqlGuidFormat.LittleEndianBinary16 || m_connection.GuidFormat == MySqlGuidFormat.TimeSwapBinary16)))
						{
							AddColumnMapping(columnMappings, addDefaultMappings, i, destinationColumn, $"@`\uE002\bcol{i}`", $"%COL% = UNHEX(%VAR%)");
						}
						else if (addDefaultMappings)
						{
							Log.Debug("Adding default column mapping from SourceOrdinal {0} to DestinationColumn {1}", i, destinationColumn);
							columnMappings.Add(new MySqlBulkCopyColumnMapping(i, destinationColumn));
						}
					}
				}
			}

			// set columns and expressions from the column mappings
			for (var i = 0; i < m_valuesEnumerator.FieldCount; i++)
			{
				var columnMapping = columnMappings.FirstOrDefault(x => x.SourceOrdinal == i);
				if (columnMapping is null)
				{
					Log.Debug("Ignoring column with SourceOrdinal {0}", i);
					bulkLoader.Columns.Add("@`\uE002\bignore`");
				}
				else
				{
					if (columnMapping.DestinationColumn.Length == 0)
						throw new InvalidOperationException("MySqlBulkCopyColumnMapping.DestinationName is not set for SourceOrdinal {0}".FormatInvariant(columnMapping.SourceOrdinal));
					if (columnMapping.DestinationColumn[0] == '@')
						bulkLoader.Columns.Add(columnMapping.DestinationColumn);
					else
						bulkLoader.Columns.Add(QuoteIdentifier(columnMapping.DestinationColumn));
					if (columnMapping.Expression is object)
						bulkLoader.Expressions.Add(columnMapping.Expression);
				}
			}

			foreach (var columnMapping in columnMappings)
			{
				if (columnMapping.SourceOrdinal < 0 || columnMapping.SourceOrdinal >= m_valuesEnumerator.FieldCount)
					throw new InvalidOperationException("SourceOrdinal {0} is an invalid value".FormatInvariant(columnMapping.SourceOrdinal));
			}

			var rowsInserted = await bulkLoader.LoadAsync(ioBehavior, cancellationToken).ConfigureAwait(false);

			if (closeConnection)
				m_connection.Close();

			Log.Info("Finished bulk copy to {0}", tableName);

			if (!m_wasAborted && rowsInserted != RowsCopied)
			{
				Log.Error("Bulk copy to DestinationTableName={0} failed; RowsCopied={1}; RowsInserted={2}", tableName, RowsCopied, rowsInserted);
				throw new MySqlException(MySqlErrorCode.BulkCopyFailed, "{0} rows were copied to {1} but only {2} were inserted.".FormatInvariant(RowsCopied, tableName, rowsInserted));
			}

#if !NETSTANDARD2_1 && !NETCOREAPP3_0
			return default;
#endif

			static string QuoteIdentifier(string identifier) => "`" + identifier.Replace("`", "``") + "`";

			static void AddColumnMapping(List<MySqlBulkCopyColumnMapping> columnMappings, bool addDefaultMappings, int destinationOrdinal, string destinationColumn, string variableName, string expression)
			{
				expression = expression.Replace("%COL%", "`" + destinationColumn + "`").Replace("%VAR%", variableName);
				var columnMapping = columnMappings.FirstOrDefault(x => destinationColumn.Equals(x.DestinationColumn, StringComparison.OrdinalIgnoreCase));
				if (columnMapping is object)
				{
					if (columnMapping.Expression is object)
					{
						Log.Warn("Column mapping for SourceOrdinal {0}, DestinationColumn {1} already has Expression {2}", columnMapping.SourceOrdinal, destinationColumn, columnMapping.Expression);
					}
					else
					{
						Log.Debug("Setting expression to map SourceOrdinal {0} to DestinationColumn {1}", columnMapping.SourceOrdinal, destinationColumn);
						columnMappings.Remove(columnMapping);
						columnMappings.Add(new MySqlBulkCopyColumnMapping(columnMapping.SourceOrdinal, variableName, expression));
					}
				}
				else if (addDefaultMappings)
				{
					Log.Debug("Adding default column mapping from SourceOrdinal {0} to DestinationColumn {1}", destinationOrdinal, destinationColumn);
					columnMappings.Add(new MySqlBulkCopyColumnMapping(destinationOrdinal, variableName, expression));
				}
			}
		}

		internal async Task SendDataReaderAsync(IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			// rent a buffer that can fit in one packet
			const int maxLength = 16_777_200;
			var buffer = ArrayPool<byte>.Shared.Rent(maxLength + 1);
			var outputIndex = 0;

			// allocate a reusable MySqlRowsCopiedEventArgs if event notification is necessary
			RowsCopied = 0;
			MySqlRowsCopiedEventArgs? eventArgs = null;
			if (NotifyAfter > 0 && MySqlRowsCopied is object)
				eventArgs = new MySqlRowsCopiedEventArgs();

			try
			{
				var values = new object?[m_valuesEnumerator!.FieldCount];
				while (true)
				{
					var hasMore = ioBehavior == IOBehavior.Asynchronous ?
						await m_valuesEnumerator.MoveNextAsync().ConfigureAwait(false) :
						m_valuesEnumerator.MoveNext();
					if (!hasMore)
						break;

					m_valuesEnumerator.GetValues(values);
					retryRow:
					var startOutputIndex = outputIndex;
					var wroteRow = true;
					var shouldAppendSeparator = false;
					foreach (var value in values)
					{
						if (shouldAppendSeparator)
							buffer[outputIndex++] = (byte) '\t';
						else
							shouldAppendSeparator = true;

						if (outputIndex >= maxLength || !WriteValue(m_connection, value, buffer.AsSpan(0, maxLength).Slice(outputIndex), out var bytesWritten))
						{
							wroteRow = false;
							break;
						}
						outputIndex += bytesWritten;
					}

					if (!wroteRow)
					{
						if (startOutputIndex == 0)
							throw new NotSupportedException("Total row length must be less than 16MiB.");
						var payload = new PayloadData(new ArraySegment<byte>(buffer, 0, startOutputIndex));
						await m_connection.Session.SendReplyAsync(payload, ioBehavior, cancellationToken).ConfigureAwait(false);
						outputIndex = 0;
						goto retryRow;
					}
					else
					{
						buffer[outputIndex++] = (byte) '\n';

						RowsCopied++;
						if (eventArgs is object && RowsCopied % NotifyAfter == 0)
						{
							eventArgs.RowsCopied = RowsCopied;
							MySqlRowsCopied!(this, eventArgs);
							if (eventArgs.Abort)
								break;
						}
					}
				}

				if (outputIndex != 0 && !(eventArgs?.Abort ?? false))
				{
					var payload2 = new PayloadData(new ArraySegment<byte>(buffer, 0, outputIndex));
					await m_connection.Session.SendReplyAsync(payload2, ioBehavior, cancellationToken).ConfigureAwait(false);
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
				m_wasAborted = eventArgs?.Abort ?? false;
			}

			static bool WriteValue(MySqlConnection connection, object? value, Span<byte> output, out int bytesWritten)
			{
				if (value is null || value == DBNull.Value)
				{
					if (output.Length < EscapedNull.Length)
					{
						bytesWritten = 0;
						return false;
					}
					EscapedNull.CopyTo(output);
					bytesWritten = EscapedNull.Length;
					return true;
				}
				else if (value is string stringValue)
				{
					return WriteString(stringValue, output, out bytesWritten);
				}
				else if (value is char charValue)
				{
					return WriteString(charValue.ToString(), output, out bytesWritten);
				}
				else if (value is byte byteValue)
				{
					return Utf8Formatter.TryFormat(byteValue, output, out bytesWritten);
				}
				else if (value is sbyte sbyteValue)
				{
					return Utf8Formatter.TryFormat(sbyteValue, output, out bytesWritten);
				}
				else if (value is short shortValue)
				{
					return Utf8Formatter.TryFormat(shortValue, output, out bytesWritten);
				}
				else if (value is ushort ushortValue)
				{
					return Utf8Formatter.TryFormat(ushortValue, output, out bytesWritten);
				}
				else if (value is int intValue)
				{
					return Utf8Formatter.TryFormat(intValue, output, out bytesWritten);
				}
				else if (value is uint uintValue)
				{
					return Utf8Formatter.TryFormat(uintValue, output, out bytesWritten);
				}
				else if (value is long longValue)
				{
					return Utf8Formatter.TryFormat(longValue, output, out bytesWritten);
				}
				else if (value is ulong ulongValue)
				{
					return Utf8Formatter.TryFormat(ulongValue, output, out bytesWritten);
				}
				else if (value is decimal decimalValue)
				{
					return Utf8Formatter.TryFormat(decimalValue, output, out bytesWritten);
				}
				else if (value is byte[] || value is ReadOnlyMemory<byte> || value is Memory<byte> || value is ArraySegment<byte> || value is MySqlGeometry)
				{
					var inputSpan = value is byte[] byteArray ? byteArray.AsSpan() :
						value is ArraySegment<byte> arraySegment ? arraySegment.AsSpan() :
						value is Memory<byte> memory ? memory.Span :
						value is MySqlGeometry geometry ? geometry.Value :
						((ReadOnlyMemory<byte>) value).Span;

					return WriteBytes(inputSpan, output, out bytesWritten);
				}
				else if (value is bool boolValue)
				{
					if (output.Length < 1)
					{
						bytesWritten = 0;
						return false;
					}
					output[0] = boolValue ? (byte) '1' : (byte) '0';
					bytesWritten = 1;
					return true;
				}
				else if (value is float || value is double)
				{
					// NOTE: Utf8Formatter doesn't support "R"
					return WriteString("{0:R}".FormatInvariant(value), output, out bytesWritten);
				}
				else if (value is MySqlDateTime mySqlDateTimeValue)
				{
					if (mySqlDateTimeValue.IsValidDateTime)
						return WriteString("{0:yyyy'-'MM'-'dd' 'HH':'mm':'ss'.'ffffff}".FormatInvariant(mySqlDateTimeValue.GetDateTime()), output, out bytesWritten);
					else
						return WriteString("0000-00-00", output, out bytesWritten);
				}
				else if (value is DateTime dateTimeValue)
				{
					if (connection.DateTimeKind == DateTimeKind.Utc && dateTimeValue.Kind == DateTimeKind.Local)
						throw new MySqlException("DateTime.Kind must not be Local when DateTimeKind setting is Utc");
					else if (connection.DateTimeKind == DateTimeKind.Local && dateTimeValue.Kind == DateTimeKind.Utc)
						throw new MySqlException("DateTime.Kind must not be Utc when DateTimeKind setting is Local");

					return WriteString("{0:yyyy'-'MM'-'dd' 'HH':'mm':'ss'.'ffffff}".FormatInvariant(dateTimeValue), output, out bytesWritten);
				}
				else if (value is DateTimeOffset dateTimeOffsetValue)
				{
					// store as UTC as it will be read as such when deserialized from a timespan column
					return WriteString("{0:yyyy'-'MM'-'dd' 'HH':'mm':'ss'.'ffffff}".FormatInvariant(dateTimeOffsetValue.UtcDateTime), output, out bytesWritten);
				}
				else if (value is TimeSpan ts)
				{
					var isNegative = false;
					if (ts.Ticks < 0)
					{
						isNegative = true;
						ts = TimeSpan.FromTicks(-ts.Ticks);
					}
					return WriteString("{0}{1}:{2:mm':'ss'.'ffffff}'".FormatInvariant(isNegative ? "-" : "", ts.Days * 24 + ts.Hours, ts), output, out bytesWritten);
				}
				else if (value is Guid guidValue)
				{
					if (connection.GuidFormat == MySqlGuidFormat.Binary16 ||
						connection.GuidFormat == MySqlGuidFormat.TimeSwapBinary16 ||
						connection.GuidFormat == MySqlGuidFormat.LittleEndianBinary16)
					{
						var bytes = guidValue.ToByteArray();
						if (connection.GuidFormat == MySqlGuidFormat.LittleEndianBinary16)
						{
							Utility.SwapBytes(bytes, 0, 3);
							Utility.SwapBytes(bytes, 1, 2);
							Utility.SwapBytes(bytes, 4, 5);
							Utility.SwapBytes(bytes, 6, 7);

							if (connection.GuidFormat == MySqlGuidFormat.TimeSwapBinary16)
							{
								Utility.SwapBytes(bytes, 0, 4);
								Utility.SwapBytes(bytes, 1, 5);
								Utility.SwapBytes(bytes, 2, 6);
								Utility.SwapBytes(bytes, 3, 7);
								Utility.SwapBytes(bytes, 0, 2);
								Utility.SwapBytes(bytes, 1, 3);
							}
						}
						return WriteBytes(bytes, output, out bytesWritten);
					}
					else
					{
						var is32Characters = connection.GuidFormat == MySqlGuidFormat.Char32;
						return Utf8Formatter.TryFormat(guidValue, output, out bytesWritten, is32Characters ? 'N' : 'D');
					}
				}
				else if (value is Enum)
				{
					return WriteString("{0:d}".FormatInvariant(value), output, out bytesWritten);
				}
				else
				{
					throw new NotSupportedException("Type {0} not currently supported. Value: {1}".FormatInvariant(value.GetType().Name, value));
				}
			}

			static bool WriteString(string value, Span<byte> output, out int bytesWritten)
			{
				var index = 0;
				bytesWritten = 0;
				while (index < value.Length)
				{
					if (Array.IndexOf(s_specialCharacters, value[index]) != -1)
					{
						if (output.Length < 2)
						{
							bytesWritten = 0;
							return false;
						}

						output[0] = (byte) '\\';
						output[1] = (byte) value[index];
						output = output.Slice(2);
						bytesWritten += 2;
						index++;
					}
					else
					{
						var nextIndex = value.IndexOfAny(s_specialCharacters, index);
						if (nextIndex == -1)
							nextIndex = value.Length;
						var encodedSize = Encoding.UTF8.GetByteCount(value.AsSpan(index, nextIndex - index));
						if (encodedSize > output.Length)
						{
							bytesWritten = 0;
							return false;
						}
						var encodedBytesWritten = Encoding.UTF8.GetBytes(value.AsSpan(index, nextIndex - index), output);
						bytesWritten += encodedBytesWritten;
						output = output.Slice(encodedBytesWritten);
						index = nextIndex;
					}
				}

				return true;
			}

			static bool WriteBytes(ReadOnlySpan<byte> value, Span<byte> output, out int bytesWritten)
			{
				if (output.Length < value.Length * 2)
				{
					bytesWritten = 0;
					return false;
				}

				foreach (var by in value)
				{
					WriteNibble(by >> 4, output);
					WriteNibble(by & 0xF, output.Slice(1));
					output = output.Slice(2);
				}

				bytesWritten = value.Length * 2;
				return true;
			}

			static void WriteNibble(int value, Span<byte> output) => output[0] = value < 10 ? (byte) (value + 0x30) : (byte) (value + 0x57);
		}

		private static ReadOnlySpan<byte> EscapedNull => new byte[] { 0x5C, 0x4E };
		private static readonly char[] s_specialCharacters = new char[] { '\t', '\\', '\n' };
		private static readonly IMySqlConnectorLogger Log = MySqlConnectorLogManager.CreateLogger(nameof(MySqlBulkCopy));

		readonly MySqlConnection m_connection;
		readonly MySqlTransaction? m_transaction;
		IValuesEnumerator? m_valuesEnumerator;
		bool m_wasAborted;
	}
}
