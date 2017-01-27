﻿namespace JMMServer.API.Model.core
{
    /// <summary>
    /// This is a class to which request should be bind to harvers parameters send to api
    /// </summary>
    public class API_Call_Parameters
    {
        /// <summary>
        /// String used in searching
        /// </summary>
        public string query { get; set; }

        /// <summary>
        /// Maximum number of items to return
        /// </summary>
        public int limit = 0;

        /// <summary>
        /// For tag searching, max number of tags to return. It will take limit and override if this is specified
        /// </summary>
        public int limit_tag = 0;

        /// <summary>
        /// the id of the filter 'this' is or resides in
        /// </summary>
        public int filter = 0;

        /// <summary>
        /// whether or not to search tags as well in search
        /// </summary>
        public int tags = 0;

        /// <summary>
        /// For searching, enable or disable fuzzy searching
        /// </summary>
        public bool fuzzy = true;

        /// <summary>
        /// Disable cast in Serie result
        /// </summary>
        public bool nocast = false;

        /// <summary>
        /// Disable genres/tags in Serie result
        /// </summary>
        public bool notag = false;
        
        /// <summary>
        /// Identyfication number of object
        /// </summary>
        public int id { get; set; }
        
        /// <summary>
        /// Rating value used in voting
        /// </summary>
        public int score { get; set; }

        /// <summary>
        /// Paging offset (the number of first item to return) using with limit help to send more narrow data
        /// </summary>
        public int offset = 0;

        /// <summary>
        /// Level of recursive building objects (ex. for Serie with level=2 return will contain serie with all episodes but without rawfile in episodes)
        /// </summary>
        public int level = 0;

        /// <summary>
        /// If set to 1 then series will contain all known episodes (not only the one in collection)
        /// </summary>
        public bool all = false;
    }
}