﻿using FluentNHibernate.Mapping;
using Shoko.Models.Server;
using Shoko.Server.Entities;

namespace Shoko.Server.Mappings
{
    public class AniDB_Anime_TitleMap : ClassMap<SVR_AniDB_Anime_Title>
    {
        public AniDB_Anime_TitleMap()
        {
            Not.LazyLoad();
            Id(x => x.AniDB_Anime_TitleID);

            Map(x => x.AnimeID).Not.Nullable();
            Map(x => x.Language).Not.Nullable();
            Map(x => x.Title).Not.Nullable();
            Map(x => x.TitleType).Not.Nullable();
        }
    }
}