﻿/*
    Copyright (C) 2014 Omega software d.o.o.

    This file is part of Rhetos.

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as
    published by the Free Software Foundation, either version 3 of the
    License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Data.Entity.Infrastructure.Interception;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.Common;
using System.Data;
using System.Text.RegularExpressions;

namespace Rhetos.Dom.DefaultConcepts.Persistence
{
    /// <summary>
    /// Based on http://www.entityframework.info/Home/FullTextSearch.
    /// This interceptor modifies SQL query generated by FullTextSearchId function mapping in DatabaseExtensionFunctionsMapping.
    /// </summary>
    public class FullTextSearchInterceptor : IDbCommandInterceptor
    {
        #region IDbCommandInterceptor implementation

        public void NonQueryExecuted(DbCommand command, DbCommandInterceptionContext<int> interceptionContext)
        {
        }

        public void NonQueryExecuting(DbCommand command, DbCommandInterceptionContext<int> interceptionContext)
        {
        }

        public void ReaderExecuted(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext)
        {
        }

        public void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext)
        {
            RewriteFullTextQuery(command);
        }

        public void ScalarExecuted(DbCommand command, DbCommandInterceptionContext<object> interceptionContext)
        {
        }

        public void ScalarExecuting(DbCommand command, DbCommandInterceptionContext<object> interceptionContext)
        {
            RewriteFullTextQuery(command);
        }

        #endregion

        public static readonly string InterceptorTag = "#FTS interceptor#";

        private static void RewriteFullTextQuery(DbCommand cmd)
        {
            if (cmd.CommandText.Contains(InterceptorTag))
            {
                var parseFtsQuery = new Regex(@"\(\(NEWID\(\)\) = (?<itemId>.+?)\) AND \(\((?<pattern>.*?) \+ '#' \+ (?<table>.*?) \+ '#' \+ (?<searchColumns>.*?)\) = '" + InterceptorTag + @"'\)", RegexOptions.Singleline);

                var ftsQueries = parseFtsQuery.Matches(cmd.CommandText).Cast<Match>();

                var itemIdFormat = new Regex(@"^(\[\w+\]|\w+)(\.(\[\w+\]|\w+))+$", RegexOptions.Singleline); // Multi-part identifier.
                var tableFormat = new Regex(@"^N'(?<unqouted>(\[\w+\]|\w+)(\.(\[\w+\]|\w+))+)'$", RegexOptions.Singleline); // Quoted multi-part identifier.
                var columnsFormat = new Regex(@"^N'(?<unqouted>(\w+|\(\w+(\s*,\s*\w+)*\)|\*))'$", RegexOptions.Singleline); // Quoted column, column list or *.
                var patternFormat = new Regex(@"^(\@\w+|N'([^']*('')*)*')$", RegexOptions.Singleline); // SQL query parameter or quoted string without quotes.

                foreach (var ftsQuery in ftsQueries.OrderByDescending(m => m.Index))
                {
                    // T-SQL CONTAINSTABLE function does not support parameters for table name and columns list,
                    // so the LINQ query must contain string literals for this interceptor to construct a valid SQL subquery with CONTAINSTABLE.
                    // Formatting validations are used here to avoid SQL injection.

                    string itemId = ftsQuery.Groups["itemId"].Value;
                    string table = ftsQuery.Groups["table"].Value;
                    string searchColumns = ftsQuery.Groups["searchColumns"].Value;
                    string pattern = ftsQuery.Groups["pattern"].Value;

                    if (!itemIdFormat.IsMatch(itemId))
                        throw new FrameworkException("Invalid FTS item ID format: '" + itemId + "'. Supported format is '" + itemIdFormat.ToString() + "'.");

                    if (!tableFormat.IsMatch(table))
                        if (table.StartsWith("@"))
                            throw new FrameworkException("Invalid FTS table name format. The table name must be a string literal in the LINQ query or expression, not a variable.");
                        else
                            throw new FrameworkException("Invalid FTS table name format: '" + table + "'. Supported format is '" + tableFormat.ToString() + "'.");

                    if (!columnsFormat.IsMatch(searchColumns))
                        if (searchColumns.StartsWith("@"))
                            throw new FrameworkException("Invalid FTS columns list format. The columns list must be a string literal in the LINQ query or expression, not a variable.");
                        else
                            throw new FrameworkException("Invalid FTS columns list format: '" + searchColumns + "'. Supported format is '" + columnsFormat.ToString() + "'.");

                    if (!patternFormat.IsMatch(pattern))
                        if (pattern == "CAST(NULL AS varchar(1))")
                            throw new FrameworkException("Invalid FTS search pattern format. Search pattern must not be NULL.");
                        else
                            throw new FrameworkException("Invalid FTS search pattern format: '" + pattern + "'. Supported format is '" + patternFormat.ToString() + "'.");

                    string ftsSql = string.Format("{0} IN (SELECT [KEY] FROM CONTAINSTABLE({1}, {2}, {3}))",
                        itemId,
                        tableFormat.Match(table).Groups["unqouted"].Value,
                        columnsFormat.Match(searchColumns).Groups["unqouted"].Value,
                        pattern);

                    cmd.CommandText =
                        cmd.CommandText.Substring(0, ftsQuery.Index)
                        + ftsSql
                        + cmd.CommandText.Substring(ftsQuery.Index + ftsQuery.Length);
                }
            }
            if (cmd.CommandText.Contains(InterceptorTag))
                throw new FrameworkException("Error while parsing FTS query. Not all search conditions were handled.");
        }
    }
}